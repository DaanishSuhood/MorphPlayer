using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MorphPlayer;

/// <summary>The one HttpClient MorphPlayer uses: the update check, fetching yt-dlp and Deno, and web thumbnails.</summary>
static partial class Web
{
    public static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MorphPlayer/{UpdateChecker.CurrentVersion}");
        return client;
    }

    /// <summary>
    /// Downloads url to path and checks it against the SHA-256 published at checksumUrl. It is written to a .partial
    /// file first and only renamed once it checks out, so a broken download never replaces a working copy.
    /// </summary>
    public static async Task DownloadVerifiedAsync(string url, string checksumUrl, string fileName, string path,
                                                   IProgress<int>? percent, CancellationToken cancel)
    {
        var expected = ExpectedHash(await Client.GetStringAsync(checksumUrl, cancel), fileName)
                       ?? throw new IOException($"No checksum for {fileName} at {checksumUrl}.");
        var partial = path + ".partial";
        try
        {
            using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancel);
                await using var target = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0 && (int)(done * 100 / total) is var p && p != lastPercent) percent?.Report(lastPercent = p);
                }
            }
            string actual;
            await using (var file = File.OpenRead(partial))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
            if (actual != expected)
                throw new IOException($"{fileName} failed its SHA-256 check (expected {expected}, got {actual}).");
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// The hash for fileName in a checksum file: a sha256sum-style line ending in its name, or else the only hash in
    /// the file (Deno publishes one per download, in PowerShell's Get-FileHash layout).
    /// </summary>
    static string? ExpectedHash(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && Sha256().IsMatch(parts[0]))
                return parts[0].ToLowerInvariant();
        }
        var all = Sha256().Matches(text);
        return all.Count == 1 ? all[0].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();
}
