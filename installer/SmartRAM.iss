#define MyAppName "RED RAM"
#define MyAppVersion "0.9.0"
#define MyAppPublisher "RED RAM"
#define MyAppExeName "RedRAM.exe"
#define MyAppId "{CDE6B25E-3D9D-4A47-9E72-6F918B7A0D31}"
[Setup]
AppId={{CDE6B25E-3D9D-4A47-9E72-6F918B7A0D31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\RED RAM
DefaultGroupName=RED RAM
OutputDir=..\installer-output
OutputBaseFilename=RED-RAM-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
DirExistsWarning=auto
[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\RED RAM"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\RED RAM"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch SmartRAM"; Flags: nowait postinstall skipifsilent
[Code]
var PreviousVersion: String;
function InitializeSetup(): Boolean;
var Key: String;
begin
 Result := True;
 Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppId}_is1';
 if RegQueryStringValue(HKLM64, Key, 'DisplayVersion', PreviousVersion) then
 begin
  Log('RED RAM upgrade detected. Installed version: ' + PreviousVersion + '; incoming: {#MyAppVersion}');
  if CompareText(PreviousVersion, '{#MyAppVersion}') = 0 then
   Log('Same RED RAM version is being repaired/reinstalled.');
 end;
end;
procedure InitializeWizard();
begin
 if PreviousVersion <> '' then
  WizardForm.Caption := 'Upgrade RED RAM ' + PreviousVersion + ' to {#MyAppVersion}';
end;
