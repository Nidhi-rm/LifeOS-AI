#define AppName "LifeOS"
#define AppVersion "1.0.0"
#define AppExeName "LifeOS.Desktop.exe"

[Setup]
AppId={{D6A60364-AE83-4BEC-A92B-99D4E95B7019}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=LifeOS
DefaultDirName={autopf}\LifeOS
DefaultGroupName=LifeOS
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=output
OutputBaseFilename=LifeOS-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LifeOS"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\LifeOS"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch LifeOS"; Flags: postinstall nowait skipifsilent
