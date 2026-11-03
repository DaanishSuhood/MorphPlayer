using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MorphPlayer;

/// <summary>
/// Hands LibVLC the streams yt-dlp finds through a small HTTP server on 127.0.0.1. YouTube throttles a request for a
/// whole file to a trickle (a long video's audio came at 8 KB/s, against 7 MB/s for a byte range), and LibVLC asks
/// for whole files, so long videos took 20 seconds or more to start. The relay fetches the same bytes in 10 MB
/// ranges instead, as yt-dlp does when it downloads, and passes them on as they arrive. A seek is simply a new
/// request from LibVLC, starting at the new point.
/// </summary>
sealed partial class StreamRelay : IDisposable
{
    const long ChunkSize = 10 * 1024 * 1024; // yt-dlp's own http_chunk_size for YouTube

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    // Only addresses registered here are served, under ids nobody can guess: never an open proxy.
    readonly ConcurrentDictionary<string, Upstream> _streams = new();
    bool _started;

    sealed record Upstream(string Url, IReadOnlyDictionary<string, string> Headers);

    /// <summary>The local address LibVLC should open in place of url.</summary>
    public string Register(string url, IReadOnlyDictionary<string, string> headers)
    {
        if (!_started)
        {
            _listener.Start();
            _started = true;
            _ = AcceptLoop();
        }
        var id = Guid.NewGuid().ToString("N");
        _streams[id] = new Upstream(url, headers);
        return $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{id}";
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_started) _listener.Stop();
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Serve(client);
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var net = client.GetStream();
                var head = await ReadHead(net);
                var request = RequestLine().Match(head);
                if (!request.Success || !_streams.TryGetValue(request.Groups[2].Value, out var upstream))
                {
                    await WriteHead(net, "404 Not Found", []);
                    return;
                }
                var headOnly = request.Groups[1].Value == "HEAD";
                var range = RangeHeader().Match(head);
                long start = range.Success ? long.Parse(range.Groups[1].Value) : 0;
                long? wantedEnd = range.Success && range.Groups[2].Length > 0 ? long.Parse(range.Groups[2].Value) : null;
                await Relay(net, upstream, start, wantedEnd, ranged: range.Success, headOnly);
            }
            catch (Exception e) when (e is IOException or SocketException or HttpRequestException
                                          or OperationCanceledException or ObjectDisposedException or FormatException)
            {
                // LibVLC hung up (it does on every seek), or the site did. Either way this request is over.
            }
        }
    }

    async Task Relay(NetworkStream net, Upstream upstream, long start, long? wantedEnd, bool ranged, bool headOnly)
    {
        using var first = await Fetch(upstream, start, Math.Min(wantedEnd ?? long.MaxValue, start + ChunkSize - 1));
        if (first.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            await WriteHead(net, "416 Range Not Satisfiable", []);
            return;
        }
        var type = first.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (first.StatusCode != HttpStatusCode.PartialContent || first.Content.Headers.ContentRange?.Length is not { } total)
        {
            // The site ignores ranges, or failed: pass its answer on as it is.
            List<(string, string)> passed = [("Content-Type", type)];
            if (first.Content.Headers.ContentLength is { } length) passed.Add(("Content-Length", length.ToString()));
            await WriteHead(net, $"{(int)first.StatusCode} {first.ReasonPhrase}", passed);
            if (!headOnly) await Copy(first, net);
            return;
        }

        var end = Math.Min(wantedEnd ?? total - 1, total - 1);
        List<(string, string)> headers =
        [
            ("Content-Type", type),
            ("Content-Length", (end - start + 1).ToString()),
            ("Accept-Ranges", "bytes"),
        ];
        if (ranged) headers.Add(("Content-Range", $"bytes {start}-{end}/{total}"));
        await WriteHead(net, ranged ? "206 Partial Content" : "200 OK", headers);
        if (headOnly) return;

        var position = start + await Copy(first, net);
        while (position <= end)
        {
            using var next = await Fetch(upstream, position, Math.Min(position + ChunkSize - 1, end));
            if (next.StatusCode != HttpStatusCode.PartialContent) return; // the address expired, most likely
            var copied = await Copy(next, net);
            if (copied == 0) return;
            position += copied;
        }
    }

    async Task<HttpResponseMessage> Fetch(Upstream upstream, long from, long to)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, upstream.Url);
        // yt-dlp's headers, its User-Agent included: some sites only serve the browser they were asked as.
        foreach (var (name, value) in upstream.Headers) message.Headers.TryAddWithoutValidation(name, value);
        message.Headers.Range = new RangeHeaderValue(from, to);
        return await Web.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
    }

    async Task<long> Copy(HttpResponseMessage response, NetworkStream net)
    {
        await using var body = await response.Content.ReadAsStreamAsync(_stop.Token);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, _stop.Token)) > 0)
        {
            await net.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            total += read;
        }
        return total;
    }

    /// <summary>The request line and headers, up to the blank line that ends them.</summary>
    static async Task<string> ReadHead(NetworkStream net)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384 && await net.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    static async Task WriteHead(NetworkStream net, string status, IEnumerable<(string Name, string Value)> headers)
    {
        var text = new StringBuilder($"HTTP/1.1 {status}\r\n");
        foreach (var (name, value) in headers) text.Append(name).Append(": ").Append(value).Append("\r\n");
        // One request per connection keeps this simple; LibVLC opens a new one for each seek anyway.
        text.Append("Connection: close\r\n\r\n");
        await net.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));
    }

    [GeneratedRegex(@"^(GET|HEAD) /([0-9a-f]{32}) HTTP/1\.[01]\r\n")]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"^Range:\s*bytes=(\d+)-(\d*)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RangeHeader();
}
