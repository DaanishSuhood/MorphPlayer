using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace MorphPlayer;

public partial class App : Application
{
    public App()
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
