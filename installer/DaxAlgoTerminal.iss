; ============================================================================
;  DaxAlgo Terminal — Inno Setup installer
;
;  Installs the self-contained app (per-user, no admin needed for the app) and
;  optionally downloads + installs the external dependencies it can use:
;     * WebView2 Runtime  — required for the Charts window (usually preinstalled on Win11)
;     * Docker Desktop    — needed for the QuestDB high-performance tick store (default backend)
;
;  Build (after publishing the app to publish\DaxAlgo-Terminal):
;     iscc /DMyAppVersion=1.0.0 /DMySourceDir="..\publish\DaxAlgo-Terminal" installer\DaxAlgoTerminal.iss
;
;  Both /D defines are optional — sensible fallbacks below.
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MySourceDir
  ; Default: the staged folder produced by scripts\publish.ps1 / the release workflow.
  #define MySourceDir "C:\DaxAlgoBuild\DaxAlgo-Terminal"
#endif

#ifndef MyOutputDir
  ; Build artifacts live off the (code-only) repo drive by default. Override with
  ; /DMyOutputDir=... (the release workflow points this back inside the runner workspace).
  #define MyOutputDir "C:\DaxAlgoBuild\installer"
#endif

#define MyAppName "DaxAlgo Terminal"
#define MyAppPublisher "DaxAlgo"
#define MyAppExeName "TradingTerminal.App.exe"
#define MyAppUrl "https://github.com/dhruuvsharma/DaxAlgo-Terminal"

[Setup]
; A stable, unique AppId so upgrades replace rather than stack. Do not change between versions.
AppId={{8E3F2A91-6C4B-4D2E-9A77-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyAppVersion}

; Per-user install — no admin needed for the app itself, and the app can write its
; logs / SQLite store next to the exe. The dependency installers (Docker/WebView2)
; self-elevate via their own UAC prompt when run.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} v{#MyAppVersion}

OutputDir={#MyOutputDir}
OutputBaseFilename=DaxAlgo-Terminal-Setup-v{#MyAppVersion}
SetupIconFile=..\src\TradingTerminal.App\Icon\DaxAlgoLogo.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
; Optional external dependencies. Skipped automatically at runtime if already present.
Name: "installwebview2"; Description: "Install Microsoft WebView2 Runtime (required for the Charts window)"; GroupDescription: "Required components:"
Name: "installdocker"; Description: "Install Docker Desktop (powers the QuestDB high-performance tick store — large ~500 MB download, requires Windows virtualization / WSL2; a reboot may be needed before it runs)"; GroupDescription: "Required components:"

[Files]
; The entire published self-contained app folder (exe + runtime + appsettings + cli\).
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Backtest CLI"; Filename: "{cmd}"; Parameters: "/k cd /d ""{app}\cli"""; Comment: "Open a console in the daxalgo-backtest CLI folder"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Dependency installers run silently before launching the app. Each self-elevates via UAC.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft WebView2 Runtime..."; Check: NeedsWebView2; Flags: waituntilterminated
Filename: "{tmp}\DockerDesktopInstaller.exe"; Parameters: "install --quiet --accept-license"; StatusMsg: "Installing Docker Desktop (this can take several minutes)..."; Check: NeedsDocker; Flags: waituntilterminated
; Offer to launch on finish (skipped for /SILENT installs).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

{ ---- Dependency detection ------------------------------------------------- }

function IsWebView2Installed: Boolean;
var
  Pv: String;
begin
  // The Evergreen runtime registers its version ('pv') under the EdgeUpdate client GUID,
  // either per-machine (64-bit view -> WOW6432Node) or per-user.
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0'));
end;

function IsDockerInstalled: Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{commonpf}\Docker\Docker\Docker Desktop.exe')) or
    RegKeyExists(HKLM, 'SOFTWARE\Docker Inc.\Docker Desktop') or
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Docker Inc.\Docker Desktop');
end;

{ True only when the user opted in AND the dependency isn't already present. }
function NeedsWebView2: Boolean;
begin
  Result := WizardIsTaskSelected('installwebview2') and not IsWebView2Installed;
end;

function NeedsDocker: Boolean;
begin
  Result := WizardIsTaskSelected('installdocker') and not IsDockerInstalled;
end;

{ ---- Download wiring ------------------------------------------------------ }

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Downloaded %s', [FileName]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedAny: Boolean;
begin
  Result := True;
  // Just before files are copied, fetch any selected-but-missing dependency installers.
  if CurPageID = wpReady then
  begin
    DownloadPage.Clear;
    NeedAny := False;
    if NeedsWebView2 then
    begin
      DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
      NeedAny := True;
    end;
    if NeedsDocker then
    begin
      DownloadPage.Add('https://desktop.docker.com/win/main/amd64/Docker Desktop Installer.exe', 'DockerDesktopInstaller.exe', '');
      NeedAny := True;
    end;

    if not NeedAny then
      Exit; // nothing to download — proceed straight to install

    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Result := True;
      except
        if DownloadPage.AbortedByUser then
          Log('Dependency download cancelled by user.')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
