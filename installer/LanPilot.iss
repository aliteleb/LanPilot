#define MyAppName "LanPilot"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Ali Teleb"
#define MyAppURL "https://github.com/aliteleb/LanPilot"
#define MyAppExeName "LanPilot.exe"
#define MyServiceName "LanPilotService"

[Setup]
AppId={{8175A0B5-4807-41B2-924A-D88B72C257AF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Copyright (C) 2026 Ali Teleb
DefaultDirName={autopf}\LanPilot
DefaultGroupName=LanPilot
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=LanPilot-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\App\{#MyAppExeName}
SetupIconFile=..\src\LanPilot.App\Assets\LanPilot.ico
LicenseFile=..\LICENSE
CloseApplications=yes
CloseApplicationsFilter=LanPilot.exe
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Open-source local network and application bandwidth control
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=Copyright (C) 2026 Ali Teleb

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\package\app\*"; DestDir: "{app}\App"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\package\service\*"; DestDir: "{app}\Service"; Excludes: "*.pdb,WinDivert64.sys"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\package\service\WinDivert64.sys"; DestDir: "{app}\Service"; Flags: ignoreversion restartreplace uninsrestartdelete
Source: "..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\LanPilot"; Filename: "{app}\App\{#MyAppExeName}"
Name: "{group}\Third-Party Notices"; Filename: "{app}\THIRD_PARTY_NOTICES.txt"
Name: "{group}\LanPilot on GitHub"; Filename: "{#MyAppURL}"
Name: "{autodesktop}\LanPilot"; Filename: "{app}\App\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LanPilot"; ValueData: """{app}\App\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\Service\LanPilot.Service.exe"" start= auto DisplayName= ""LanPilot Service"""; Flags: runhidden; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "config {#MyServiceName} binPath= ""{app}\Service\LanPilot.Service.exe"" start= delayed-auto DisplayName= ""LanPilot Service"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Discovers and safely manages authorized local IPv4 devices for LanPilot."""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/5000/restart/15000/""/0"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden
Filename: "{app}\App\{#MyAppExeName}"; Description: "Open LanPilot"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden; RunOnceId: "StopLanPilotService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden; RunOnceId: "DeleteLanPilotService"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\LanPilot"; Check: ShouldDeleteData

[Code]
var
  DeleteDataOnUninstall: Boolean;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#MyServiceName}');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if ServiceExists then
  begin
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1200);
  end;
end;

function InitializeUninstall: Boolean;
begin
  DeleteDataOnUninstall :=
    MsgBox('Delete saved LanPilot devices, rules, usage history, and settings?', mbConfirmation, MB_YESNO) = IDYES;
  Result := True;
end;

function ShouldDeleteData: Boolean;
begin
  Result := DeleteDataOnUninstall;
end;
