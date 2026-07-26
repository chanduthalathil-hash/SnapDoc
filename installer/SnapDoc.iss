; SnapDoc installer script (Inno Setup 6).
; Build with: ISCC.exe SnapDoc.iss
; Produces installer\output\SnapDoc-Setup.exe
;
; Per-user install (no admin/UAC prompt) so it works even on locked-down machines --
; this is a small utility being shared informally, not something that needs a
; machine-wide, admin-owned install.

#define MyAppName "SnapDoc"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "SnapDoc"
#define MyAppExeName "SnapDoc.exe"

[Setup]
AppId={{6149E866-9855-4598-BE6E-AF3FF46B3010}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=SnapDoc-Setup
SetupIconFile=..\src\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startupicon"; Description: "Launch SnapDoc automatically when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\src\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
; SnapDoc lives in the tray with no window on launch -- offer to start it right after
; setup finishes so the user gets some immediate confirmation it's actually running.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent
