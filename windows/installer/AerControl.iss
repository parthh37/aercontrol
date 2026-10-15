; AerControl setup (Inno Setup 6.3+). Built by scripts\build-installer.ps1:
;   ISCC /DAppVersion=1.2.0 /DPlatform=x64 /DSourceDir=<dist\AerControl_x64> /DOutputDir=<release> AerControl.iss
;
; The app itself stays an MSIX (the widget needs package identity, and the .appinstaller gives
; automatic updates), so this setup is a friendly wrapper around install.ps1: it unpacks the
; signed packages to a temp folder, runs the script, and offers to launch the app. It registers
; no uninstaller of its own; AerControl is removed from Settings > Apps like any MSIX app.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Platform
  #define Platform "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<path to dist\AerControl_<Platform>>
#endif
#ifndef OutputDir
  #define OutputDir "..\release"
#endif

#if Platform == "ARM64"
  #define DepArch "arm64"
  #define Allowed "arm64"
#else
  #define DepArch "x64"
  #define Allowed "x64compatible"
#endif

[Setup]
AppId={{6E400000-B5A3-F393-E0A9-AE7C0A7201A5}
AppName=AerControl
AppVersion={#AppVersion}
AppVerName=AerControl {#AppVersion}
AppPublisher=AerControl
AppPublisherURL=https://github.com/parthh37/aercontrol
AppSupportURL=https://github.com/parthh37/aercontrol/issues
VersionInfoVersion={#AppVersion}
OutputDir={#OutputDir}
OutputBaseFilename=AerControl-Setup-{#Platform}
SetupIconFile=..\src\AerControl.App\Assets\AerControl.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed={#Allowed}
MinVersion=10.0.19041
; MSIX packages install per user. install.ps1 asks for admin rights only once, to trust
; the signing certificate, so the setup itself runs unelevated as the signed-in user.
PrivilegesRequired=lowest
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
CloseApplications=no
SetupLogging=yes

[Messages]
FinishedHeadingLabel=AerControl is installed
FinishedLabelNoIcons=AerControl is in your Start menu. On Windows 11, add the widget from the Widgets board (Win+W > Add widgets).

[Files]
Source: "{#SourceDir}\*.msix"; DestDir: "{tmp}\pkg"; Flags: ignoreversion
Source: "{#SourceDir}\Dependencies\{#DepArch}\*.msix"; DestDir: "{tmp}\pkg\Dependencies\{#DepArch}"; Flags: ignoreversion
Source: "{#SourceDir}\*.cer"; DestDir: "{tmp}\pkg"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\*.appinstaller"; DestDir: "{tmp}\pkg"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\scripts\install.ps1"; DestDir: "{tmp}\pkg"; Flags: ignoreversion

[Run]
; MSIX apps launch through their AppUserModelID: shell:AppsFolder\<PackageFamilyName>!App
Filename: "powershell.exe"; Parameters: "-NoProfile -WindowStyle Hidden -Command ""Start-Process ('shell:AppsFolder\' + (Get-AppxPackage -Name AerControl).PackageFamilyName + '!App')"""; Description: "Launch AerControl"; Flags: postinstall nowait skipifsilent runhidden; Check: Installed

[Code]
var
  InstallOk: Boolean;

function Installed: Boolean;
begin
  Result := InstallOk;
end;

function LogPath: String;
begin
  Result := ExpandConstant('{localappdata}\AerControl\install.log');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Installing the AerControl package (approve the certificate prompt if asked)...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  InstallOk := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\pkg\install.ps1') + '" -LogPath "' + LogPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  WizardForm.ProgressGauge.Style := npbstNormal;
  if not InstallOk then
    SuppressibleMsgBox('AerControl could not be installed (exit code ' + IntToStr(Code) + ').' + #13#10#13#10 +
      'Details are in:' + #13#10 + LogPath, mbError, MB_OK, IDOK);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not InstallOk then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Installation failed';
    WizardForm.FinishedLabel.Caption := 'See ' + LogPath + ' for details, then run setup again.';
  end;
end;
