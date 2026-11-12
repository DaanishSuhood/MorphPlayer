using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace MorphPlayer;

/// <summary>
/// Keeps MorphPlayer to one window. The first copy to start holds a named mutex and listens on a named pipe; a copy
/// started while it runs (a double-click in Explorer, "Open with", a link on the command line) sends its arguments
/// down the pipe and exits, and the first copy opens them. Both names include the Windows session and user, so two
/// people signed in to the same PC each get their own player.
/// </summary>
sealed class SingleInstance : IDisposable
{
    /// <summary>On the command line, starts a separate window anyway.</summary>
    public const string NewWindowSwitch = "--new-window";

    static readonly string Name = $"MorphPlayer-{Process.GetCurrentProcess().SessionId}-"
                                  + Environment.UserName.Replace('\\', '_').Replace('/', '_');
    // Short, because the caller tries again: see App.OnStartup.
    const int ConnectTimeoutMs = 250;
    const int ReadTimeoutMs = 5000;
    const int AsfwAny = -1;
    const int SwRestore = 9;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);

    readonly CancellationTokenSource _stop = new();
    Mutex? _mutex;

    /// <summary>True if no other copy is running, in which case this one is now the copy the others report to.</summary>
    public bool TryClaim()
    {
        // Only whether the mutex already existed matters, not who owns it; Windows removes it when the process that
        // made it exits, however it exits.
        var mutex = new Mutex(initiallyOwned: false, @"Local\" + Name, out var createdNew);
        if (createdNew)
            _mutex = mutex;
        else
            mutex.Dispose();
        return createdNew;
    }

    /// <summary>
    /// Hands the arguments to the copy already running. False if it didn't answer within a quarter of a second: it
    /// may be busy with another copy, closing, or running as administrator while this copy isn't.
    /// </summary>
    public static bool Send(string[] args)
    {
        try
        {
            // Windows only lets a process bring its window forward if it was just started by the user, as this copy
            // was; this passes that right on to the first copy.
            AllowSetForegroundWindow(AsfwAny);
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            pipe.Write(JsonSerializer.SerializeToUtf8Bytes(args));
            pipe.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Calls back, on a background thread, with the arguments of each copy started from now on.</summary>
    public void Listen(Action<string[]> received) => _ = ListenAsync(received, _stop.Token);

    static async Task ListenAsync(Action<string[]> received, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly: no other account on this PC can make MorphPlayer open something.
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                // A copy that connects and then hangs mustn't keep the others waiting.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(ReadTimeoutMs);
                var args = await JsonSerializer.DeserializeAsync<string[]>(pipe, cancellationToken: limit.Token);
                if (args != null) received(args);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A copy that vanished mid-message, or a pipe that couldn't be made: wait a moment and carry on.
                try { await Task.Delay(200, stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// File and folder names given relative to the folder a copy was started in, made absolute, because the copy
    /// that opens them runs somewhere else. Links and anything that isn't an existing path are left as they are.
    /// </summary>
    public static string[] AbsolutePaths(IEnumerable<string> args) => args.Select(arg =>
    {
        try
        {
            return File.Exists(arg) || Directory.Exists(arg) ? Path.GetFullPath(arg) : arg;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException
                                      or UnauthorizedAccessException)
        {
            return arg;
        }
    }).ToArray();

    /// <summary>Restores the window if it is minimized (to maximized or fullscreen, if that's what it was) and brings it forward.</summary>
    public static void BringToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        window.Activate();
        SetForegroundWindow(hwnd);
    }

    public void StopListening() => _stop.Cancel();

    public void Dispose()
    {
        _stop.Cancel();
        _mutex?.Dispose();
        _mutex = null;
    }
}
