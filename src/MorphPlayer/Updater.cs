using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MorphPlayer;

/// <summary>
/// Installs the latest release in place of this one. An installed copy downloads the new installer and runs it
/// silently; a portable copy downloads the new zip, and a small script copies it over this folder once MorphPlayer
/// has closed. Either way the download is checked against the release's SHA256SUMS.txt, and MorphPlayer starts
/// again afterwards. A copy that is neither (a build run from source) only gets the release page.
/// </summary>
static class Updater
{
    public enum Mode { ReleasePage, Installer, Portable }

    static readonly string AppDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MorphPlayer", "update");

    public static Mode How { get; } = Detect();

    static Mode Detect()
    {
        // Inno Setup leaves its uninstaller beside the app; publish.ps1 puts LICENSE.txt in the portable zip.
        if (File.Exists(Path.Combine(AppDir, "unins000.exe"))) return Mode.Installer;
        if (File.Exists(Path.Combine(AppDir, "LICENSE.txt")) && Directory.Exists(Path.Combine(AppDir, "libvlc"))
            && CanWriteTo(AppDir))
            return Mode.Portable;
        return Mode.ReleasePage;
    }

    static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads and checks the latest release, and returns what to start once MorphPlayer has saved its state; the
    /// caller starts it and closes the window. Throws IOException or HttpRequestException if anything is missing or
    /// fails its check.
    /// </summary>
    public static async Task<ProcessStartInfo> PrepareAsync(IProgress<int> percent, CancellationToken cancel)
    {
        if (How == Mode.ReleasePage) throw new InvalidOperationException("This copy of MorphPlayer can't update itself.");

        // The assets' own addresses, rather than ones built from the version, so a tag such as "v1.2" still works.
        using var doc = JsonDocument.Parse(await Web.Client.GetStringAsync(UpdateChecker.LatestReleaseApi, cancel));
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
        var sums = assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        var wanted = How == Mode.Installer
            ? assets.FirstOrDefault(a => a.Name.StartsWith("MorphPlayer-Setup-", StringComparison.OrdinalIgnoreCase)
                                         && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            : assets.FirstOrDefault(a => a.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase));
        if (sums.Url is not { Length: > 0 } || wanted.Url is not { Length: > 0 })
            throw new IOException("The release doesn't have the files an update needs.");

        if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, recursive: true);
        Directory.CreateDirectory(WorkDir);
        var download = Path.Combine(WorkDir, wanted.Name);
        await Web.DownloadVerifiedAsync(wanted.Url, sums.Url, wanted.Name, download, percent, cancel);

        return How == Mode.Installer ? InstallerStart(download) : await PortableStart(download, cancel);
    }

    /// <summary>
    /// The installer, silent: it shows only its progress, keeps the folder and shortcuts chosen last time, and starts
    /// MorphPlayer again afterwards (/RELAUNCH, see MorphPlayer.iss). Installed for all users, it asks for admin rights.
    /// </summary>
    static ProcessStartInfo InstallerStart(string installer)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var forAllUsers = AppDir.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(installer) { UseShellExecute = true };
        foreach (var arg in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1",
                                    forAllUsers ? "/ALLUSERS" : "/CURRENTUSER" })
            start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>
    /// Unpacks the zip next to the download, and returns a PowerShell script that waits for MorphPlayer to close,
    /// copies the new files over this folder (libvlc\ mirrored, so plugins an older LibVLC had are removed), and
    /// starts it again. Robocopy keeps each file's timestamp, which LibVLC's plugins.dat relies on.
    /// </summary>
    static async Task<ProcessStartInfo> PortableStart(string zip, CancellationToken cancel)
    {
        var unpacked = Path.Combine(WorkDir, "new");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpacked), cancel);
        // The zip holds a MorphPlayer\ folder.
        var from = Directory.Exists(Path.Combine(unpacked, "MorphPlayer")) ? Path.Combine(unpacked, "MorphPlayer") : unpacked;
        if (!File.Exists(Path.Combine(from, "MorphPlayer.exe")) || !Directory.Exists(Path.Combine(from, "libvlc")))
            throw new IOException("The downloaded zip isn't a MorphPlayer folder.");

        var script = Path.Combine(WorkDir, "apply-update.ps1");
        await File.WriteAllTextAsync(script, PortableScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancel);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                                    "-File", script, "-ParentId", Environment.ProcessId.ToString(), "-From", from, "-To", AppDir,
                                    "-Exe", Path.GetFileName(Environment.ProcessPath ?? "MorphPlayer.exe"), "-Work", WorkDir })
            start.ArgumentList.Add(arg);
        return start;
    }

    const string PortableScript = """
        param([int]$ParentId, [string]$From, [string]$To, [string]$Exe, [string]$Work)
        Wait-Process -Id $ParentId -Timeout 120 -ErrorAction SilentlyContinue
        $quiet = '/R:20', '/W:1', '/NP', '/NFL', '/NDL', '/NJH', '/NJS'
        robocopy (Join-Path $From 'libvlc') (Join-Path $To 'libvlc') /MIR @quiet | Out-Null
        robocopy $From $To @quiet | Out-Null
        if ($Exe -ne 'MorphPlayer.exe' -and (Test-Path (Join-Path $To $Exe))) {
            Copy-Item (Join-Path $From 'MorphPlayer.exe') (Join-Path $To $Exe) -Force
        }
        Start-Process -FilePath (Join-Path $To $Exe) -WorkingDirectory $To
        Start-Sleep -Seconds 2
        Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
