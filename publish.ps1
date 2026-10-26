# Builds MorphPlayer for release:
#   .\publish.ps1                 -> artifacts\MorphPlayer-<version>-portable.zip (unzip and run, no .NET install needed)
#                                    artifacts\installer\MorphPlayer-Setup-<version>.exe (Inno Setup installer)
#   .\publish.ps1 -SkipInstaller  -> only the zip
# Either way it also writes artifacts\SHA256SUMS.txt, to publish with the release.
#
# MorphPlayer.exe is one self-contained file, but LibVLC can't live inside it: LibVLC loads its ~320 plugins
# from a folder on disk. So the app ships as a folder, MorphPlayer.exe plus libvlc\, zipped for the portable
# download and installed whole by the installer.
#
# The folder gets a plugins.dat cache, made by vlc-cache-gen from the official VLC build of the same version.
# Without it LibVLC loads every plugin DLL on each start to see what it does, which takes 20 seconds or more
# the first time while Windows scans them all. With it, only the plugins a file actually needs are loaded.
# ContinuousIntegrationBuild records source paths as /_/... so the build machine's folders don't end up in the exe.
param(
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\MorphPlayer\MorphPlayer.csproj'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish\MorphPlayer'

$csproj = [xml](Get-Content $project)
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$vlcVersion = ($csproj.Project.ItemGroup.PackageReference | Where-Object Include -eq 'VideoLAN.LibVLC.Windows').Version

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $project -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:ContinuousIntegrationBuild=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# --- plugins.dat -------------------------------------------------------------------------------------------
$arch = $Runtime -replace '^win-', ''
$vlcZipName = "vlc-$vlcVersion-win$($arch -replace 'x', '')"   # win-x64 -> vlc-3.0.24-win64
$vlcUrl = "https://download.videolan.org/pub/videolan/vlc/$vlcVersion/win$($arch -replace 'x', '')/$vlcZipName.zip"
$cacheDir = Join-Path $artifacts 'cache'
$vlcZip = Join-Path $cacheDir "$vlcZipName.zip"
New-Item -ItemType Directory -Force $cacheDir | Out-Null

if (-not (Test-Path $vlcZip)) {
    Write-Host "Downloading $vlcUrl"
    Invoke-WebRequest $vlcUrl -OutFile "$vlcZip.partial" -UseBasicParsing
    Move-Item "$vlcZip.partial" $vlcZip
}
# Checked on every run, not just after downloading, so a corrupted cached copy is never used.
# VideoLAN serves .sha256 files as binary, so Windows PowerShell hands back bytes rather than text.
$shaContent = (Invoke-WebRequest "$vlcUrl.sha256" -UseBasicParsing).Content
if ($shaContent -is [byte[]]) { $shaContent = [Text.Encoding]::ASCII.GetString($shaContent) }
$expected = ($shaContent.Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash $vlcZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item $vlcZip
    throw "$vlcZipName.zip failed its SHA-256 check (expected $expected, got $actual). Run again to re-download."
}

# vlc-cache-gen.exe needs libvlc.dll and libvlccore.dll beside it. Using the copy from our own publish folder keeps the
# cache in step with the plugins it describes.
$genDir = Join-Path $cacheDir "cache-gen-$vlcVersion"
if (-not (Test-Path (Join-Path $genDir 'vlc-cache-gen.exe'))) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force $genDir | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($vlcZip)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'vlc-cache-gen.exe' } | Select-Object -First 1
        if (-not $entry) { throw "vlc-cache-gen.exe is not in $vlcZipName.zip." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $genDir 'vlc-cache-gen.exe'), $true)
    } finally { $zip.Dispose() }
}
$libvlcDir = Join-Path $publishDir "libvlc\$Runtime"
Copy-Item (Join-Path $libvlcDir 'libvlc.dll'), (Join-Path $libvlcDir 'libvlccore.dll') $genDir -Force
& (Join-Path $genDir 'vlc-cache-gen.exe') (Join-Path $libvlcDir 'plugins')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $libvlcDir 'plugins\plugins.dat'))) {
    throw 'vlc-cache-gen did not write plugins.dat.'
}

# The licences travel with the binaries: LibVLC's LGPL asks for that.
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $publishDir 'LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') $publishDir

# --- portable zip ------------------------------------------------------------------------------------------
# ZipFile keeps each file's timestamp, which plugins.dat relies on. The zip holds a MorphPlayer\ folder, so
# extracting it never scatters 300 files into Downloads.
$portable = Join-Path $artifacts "MorphPlayer-$version-portable.zip"
if (Test-Path $portable) { Remove-Item $portable }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $portable, [IO.Compression.CompressionLevel]::Optimal, $true)

# --- installer ---------------------------------------------------------------------------------------------
if (-not $SkipInstaller) {
    $iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        throw 'Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup -e (or use -SkipInstaller).'
    }

    & $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer\MorphPlayer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
    $installer = Join-Path $PSScriptRoot "artifacts\installer\MorphPlayer-Setup-$version.exe"
}

# Only what this run built: artifacts\installer may still hold installers from earlier versions.
$files = @(Get-Item $portable) + @(if (-not $SkipInstaller) { Get-Item $installer })

# Releases aren't code-signed, so publish checksums people can compare against
# (sha256sum format: check with `sha256sum -c SHA256SUMS.txt`, or compare with Get-FileHash).
$checksums = Join-Path $artifacts 'SHA256SUMS.txt'
$lines = $files | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
# LF line endings: sha256sum on Linux, macOS and Git Bash treats a CR as part of the file name.
[IO.File]::WriteAllText($checksums, (($lines -join "`n") + "`n"))
$lines

$files |
    Format-Table @{ n = 'File'; e = { $_.FullName.Substring($PSScriptRoot.Length + 1) } },
                 @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } }
