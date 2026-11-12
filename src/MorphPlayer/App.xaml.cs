using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MorphPlayer;

public partial class App : Application
{
    // A copy that finds another already running tries this many times, a quarter of a second each, to hand over to
    // it or, if it has gone, to take its place.
    const int HandOverAttempts = 40;

    readonly SingleInstance _instance = new();
    readonly List<string[]> _sentEarly = new(); // by other copies, before the window existed
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var newWindow = e.Args.Contains(SingleInstance.NewWindowSwitch, StringComparer.OrdinalIgnoreCase);
        var args = SingleInstance.AbsolutePaths(
            e.Args.Where(a => !a.Equals(SingleInstance.NewWindowSwitch, StringComparison.OrdinalIgnoreCase)));

        // One window: if MorphPlayer is already running, pass it what this copy was asked to open and leave. A copy
        // that doesn't answer is usually closing, and saving where its file was left, so wait for it to go and then
        // take over. If neither happens, open a window anyway rather than drop the file.
        if (!newWindow)
        {
            var first = false;
            for (var attempt = 0; attempt < HandOverAttempts && !(first = _instance.TryClaim()); attempt++)
            {
                if (!SingleInstance.Send(args)) continue;
                Shutdown();
                return;
            }
            // Listening starts before LibVLC does: from source, LibVLC's first start can take 20 seconds, and a
            // copy started meanwhile shouldn't give up on this one.
            if (first) _instance.Listen(sent => Dispatcher.InvokeAsync(() => Opened(sent)));
        }

        InitializeLibVlc();
        var state = AppState.Load();
        Themes.Apply(state.Theme, state.Accent);

        _window = new MainWindow(state, args);
        _window.Show();
        foreach (var sent in _sentEarly) _window.OpenFromAnotherCopy(sent);
        _sentEarly.Clear();
    }

    /// <summary>Another copy of MorphPlayer was started, with these arguments.</summary>
    void Opened(string[] args)
    {
        if (_window != null)
            _window.OpenFromAnotherCopy(args);
        else
            _sentEarly.Add(args);
    }

    /// <summary>
    /// Stops answering other copies, as the window closes. This copy still counts as running until its process
    /// ends, so the next one waits for the resume positions it is saving.
    /// </summary>
    public void StopTakingOpens() => _instance.StopListening();

    protected override void OnExit(ExitEventArgs e)
    {
        _instance.Dispose();
        base.OnExit(e);
    }

    static void InitializeLibVlc()
    {
        // LibVLC lives in libvlc\<arch>\ next to the exe. LibVLCSharp normally finds it from its own
        // assembly's location, which is empty inside a single-file build, so point it there directly.
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
        var libVlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", arch);
        if (Directory.Exists(libVlcDir))
            LibVLCSharp.Shared.Core.Initialize(libVlcDir);
        else
            LibVLCSharp.Shared.Core.Initialize();
    }
}
