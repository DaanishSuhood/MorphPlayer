; MorphPlayer installer (Inno Setup 6). Build it with .\publish.ps1, which publishes the app first and passes the
; version from MorphPlayer.csproj:  ISCC /DAppVersion=1.2.3 installer\MorphPlayer.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "MorphPlayer"
#define AppExe "MorphPlayer.exe"
#define AppDescription "Media player for almost any video or audio format"
#define ProgId "MorphPlayer.Media"
; Keep in step with MediaExtensions in MainWindow.xaml.cs.
#define MediaExts ".mp4 .mkv .avi .mov .wmv .webm .flv .m4v .mpg .mpeg .ts .m2ts .3gp .ogv .vob .divx .rm .rmvb .asf .mp3 .flac .wav .m4a .aac .ogg .opus .wma .alac .aiff .ape .mka"

[Setup]
; Never change AppId: it is how later versions find and upgrade this install.
AppId={{960BDB22-94F4-41E0-AD16-567122400C1A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL=https://github.com/DaanishSuhood/MorphPlayer
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; A media player needs no admin rights: install for the current user unless they pick all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\src\MorphPlayer\Assets\MorphPlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Tells Explorer to pick up the Open with entries without a sign-out.
ChangesAssociations=yes
; Close a running copy when upgrading; don't relaunch it afterwards (the finish page offers that).
CloseApplications=yes
RestartApplications=no
OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Both ticked by default.
Name: "startmenuicon"; Description: "Create a &Start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The whole publish folder: the exe plus LibVLC and its plugins. Inno keeps each file's timestamp, which
; matters: LibVLC's plugins.dat cache is only trusted while the plugin files' dates match it.
Source: "..\artifacts\publish\{#AppName}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Plugins from an older LibVLC that a new version no longer ships would otherwise be left behind.
Type: filesandordirs; Name: "{app}\libvlc"

[UninstallDelete]
; Resume positions and audio choices (state.json), and the yt-dlp and Deno it fetched (tools\).
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppDescription}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppDescription}"; Tasks: desktopicon

[Registry]
; One file type that every supported extension points to, so MorphPlayer shows up under Open with.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "Media file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
; Lists MorphPlayer in Settings > Apps > Default apps, where people choose it for each file type.
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{#AppDescription}"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; MorphPlayer's own updater runs this installer silently with /RELAUNCH=1, and expects to be started again afterwards.
; runasoriginaluser: an all-users install runs as admin, and MorphPlayer shouldn't.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[Code]
{ Windows never lets an installer make itself the default player; these entries only offer MorphPlayer as a
  choice for each extension. The per-extension values are written here rather than in [Registry] so the list
  lives in one place (MediaExts). }

function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function RootKey: Integer;
begin
  if IsAdminInstallMode then
    Result := HKLM
  else
    Result := HKCU;
end;

function NextExt(var List: String): String;
var
  Space: Integer;
begin
  List := Trim(List);
  Space := Pos(' ', List);
  if Space = 0 then
  begin
    Result := List;
    List := '';
  end
  else
  begin
    Result := Copy(List, 1, Space - 1);
    Delete(List, 1, Space);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  List, Ext: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  List := '{#MediaExts}';
  while List <> '' do
  begin
    Ext := NextExt(List);
    RegWriteStringValue(RootKey, 'Software\Classes\' + Ext + '\OpenWithProgids', '{#ProgId}', '');
    RegWriteStringValue(RootKey, 'Software\Classes\Applications\{#AppExe}\SupportedTypes', Ext, '');
    RegWriteStringValue(RootKey, 'Software\{#AppName}\Capabilities\FileAssociations', Ext, '{#ProgId}');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  List: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  { Only our own value under each extension: the extension keys belong to Windows and other apps. }
  List := '{#MediaExts}';
  while List <> '' do
    RegDeleteValue(RootKey, 'Software\Classes\' + NextExt(List) + '\OpenWithProgids', '{#ProgId}');
end;
