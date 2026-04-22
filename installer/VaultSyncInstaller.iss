#define MyAppName "VaultSync"
#define MyAppVersion "1.7.3"
#define MyAppPublisher "Flavio Giacchetti"
#define MyAppExeName "VaultSync.UI.exe"
#define AppOutputDir "..\\src\\VaultSync.UI\\bin\\Release\\net8.0-windows10.0.19041.0\\win-x64\\publish"
#define AppIconPath "..\\src\\VaultSync.UI\\Assets\\vaultsync.ico"

[Setup]
AppId={{A95C0681-2A65-4C8B-BFA9-VAULTSYNC123456}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={pf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=VaultSync-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64
; Use your app icon for the installer EXE and wizard
SetupIconFile="{#AppIconPath}"

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#AppOutputDir}\*"; \
  DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
; Start menu shortcut with app icon
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
; Desktop shortcut with app icon
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; \
  Description: "Launch {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent

[Registry]
; Associate encrypted backup archives (.vse) with VaultSync
Root: HKCR; Subkey: ".vse"; ValueType: string; ValueName: ""; ValueData: "VaultSync.EncryptedBackup"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "VaultSync.EncryptedBackup"; ValueType: string; ValueName: ""; ValueData: "VaultSync Encrypted Backup"; Flags: uninsdeletekey
Root: HKCR; Subkey: "VaultSync.EncryptedBackup\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCR; Subkey: "VaultSync.EncryptedBackup\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools"
