#define AppName "Mandela VPN"
#define AppVersion "1.0.0"
#define AppPublisher "Sekoza"
#define AppExeName "MandelaVpnGuard.exe"

[Setup]
AppId={{51C8A723-8F9B-4BE8-92F9-BE36C3DA8B10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Mandela VPN Guard
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
OutputDir=..\dist-installer
OutputBaseFilename=MandelaVpnGuard-Setup-x64
SetupIconFile=..\Assets\MandelaVpnGuard.ico
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Mandela VPN Guard Installer
VersionInfoProductName={#AppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish-installer\MandelaVpnGuard.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--shutdown"; Flags: runhidden waituntilterminated; RunOnceId: "StopMandelaVpnGuard"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""MandelaVpnGuard"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveMandelaVpnGuardStartup"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ExistingExe: String;
begin
  Result := '';
  ExistingExe := ExpandConstant('{app}\{#AppExeName}');
  if FileExists(ExistingExe) then
  begin
    if not Exec(ExistingExe, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := 'Could not close the existing Mandela VPN Guard. Close it from the tray and try again.'
    else if ResultCode <> 0 then
      Result := 'Mandela VPN Guard did not close cleanly. Close it from the tray and try again.';
  end;
end;
