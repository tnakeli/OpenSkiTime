#ifndef AppVersion
  #error AppVersion must be supplied by Package-Windows.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by Package-Windows.ps1
#endif

[Setup]
AppId={{BB6E9B20-468B-42C2-B7B4-3CA02CDD9A6E}
AppName=OpenSkiTime
AppVersion={#AppVersion}
AppVerName=OpenSkiTime {#AppVersion}
AppPublisher=OpenSkiTime contributors
AppPublisherURL=https://openskiti.me
AppSupportURL=https://github.com/tnakeli/OpenSkiTime/issues
AppUpdatesURL=https://openskiti.me/download/
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={localappdata}\Programs\OpenSkiTime
DefaultGroupName=OpenSkiTime
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=OpenSkiTime-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\OpenSkiTime.Rewrite.Desktop.exe
LicenseFile={#SourceDir}\LICENSE.txt
InfoBeforeFile={#SourceDir}\NOTICE.txt
CloseApplications=yes
RestartApplications=no
SetupLogging=no
MinVersion=10.0.22000

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\OpenSkiTime"; Filename: "{app}\OpenSkiTime.Rewrite.Desktop.exe"
Name: "{autodesktop}\OpenSkiTime"; Filename: "{app}\OpenSkiTime.Rewrite.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenSkiTime.Rewrite.Desktop.exe"; Description: "Launch OpenSkiTime"; Flags: nowait postinstall skipifsilent

; No uninstall deletion rules: event files and user preferences are never removed.
