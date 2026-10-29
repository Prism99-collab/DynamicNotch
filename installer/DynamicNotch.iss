; Dynamic Notch — Windows installer (Inno Setup 6)
; Built by scripts\build.ps1, which passes the version and the folder holding the published exes.
; Per-user install: no administrator prompt, installs to %LOCALAPPDATA%\Programs\Dynamic Notch.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
; "x64" (Intel / AMD, also runs on ARM64 PCs through emulation) or "arm64" (native Snapdragon build)
#ifndef Arch
  #define Arch "x64"
#endif

#define AppName "Dynamic Notch"
#define AppExe "DynamicNotch.exe"
#define AppPublisher "Prism99-collab"
#define AppUrl "https://github.com/Prism99-collab/DynamicNotch"

[Setup]
; Never change AppId: it is how Windows recognises upgrades of the same app.
AppId={{CD4578A0-5D80-422D-9C75-1054B3DA5884}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; per-user, no admin rights needed
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no

; one installer per CPU type: x64 (Intel / AMD, also runs on ARM64 via emulation) and native ARM64 (Snapdragon)
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

OutputDir={#OutputDir}
OutputBaseFilename=DynamicNotch-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\DynamicNotch\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=100
; follow the PC's language (French or English) instead of asking
ShowLanguageDialog=auto
; the logo in the wizard's corner, in sizes for 100 / 125 / 150 / 200 % display scaling
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-69.bmp,wizard-small-83.bmp,wizard-small-110.bmp
Compression=lzma2/max
SolidCompression=yes

; close a running copy before replacing it (upgrades), and don't ask to restart Windows
CloseApplications=force
CloseApplicationsFilter={#AppExe}
RestartApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\win-{#Arch}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Open {#AppName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Open {#AppName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; closes the notch and removes its sign-in task (the app knows exactly what it registered)
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "DynamicNotchCleanup"

[UninstallDelete]
; settings and logs
Type: filesandordirs; Name: "{userappdata}\DynamicNotch"

[Code]
procedure StopRunningCopy();
var
  ResultCode: Integer;
begin
  { make sure no copy of the notch keeps the exe locked while files are replaced }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy();
  Result := '';
end;
