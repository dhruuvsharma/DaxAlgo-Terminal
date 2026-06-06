# Installer

[`DaxAlgoTerminal.iss`](DaxAlgoTerminal.iss) is an [Inno Setup](https://jrsoftware.org/isinfo.php)
script that produces a single `DaxAlgo-Terminal-Setup-vX.Y.Z.exe`.

## What it does

- Installs the **self-contained** app **per-user** to `%LocalAppData%\Programs\DaxAlgo Terminal`
  (no admin required for the app — it can write its logs / SQLite store next to the exe).
- Creates Start-menu shortcuts (and an optional desktop shortcut), plus a shortcut into the
  `cli\` folder for the headless `daxalgo-backtest` tool.
- On the **Select Additional Tasks** page, offers to download and install the external
  dependencies the app can use:
  - **Microsoft WebView2 Runtime** — required for the Charts window (usually already present on
    Windows 11). Checked by default; skipped automatically if already installed.
  - **Docker Desktop** — powers the QuestDB high-performance tick store (the default store
    backend). **Checked by default** (large ~500 MB download, needs Windows virtualization / WSL2).
    Skipped automatically if already installed.

  Selected-but-missing dependencies are downloaded just before install and run silently. Each
  third-party installer self-elevates via its own UAC prompt. The app works even if a dependency is
  skipped or its install needs a reboot: Charts needs WebView2, and **the app always launches
  without Docker** — bars persist to SQLite and L1/L2/trade persistence simply stays off until
  QuestDB is up. Once Docker is installed and running, the login screen auto-starts the QuestDB
  container (`AutoStartDocker`, on by default) and engages tick persistence live — no app restart.
  To opt out of QuestDB entirely, set `MarketDataStore:Provider` to `Sqlite`.

## Build it

1. Publish the app to the staging folder (this is what the script bundles):

   ```powershell
   ./scripts/publish.ps1 -Version 1.0.0 -Installer
   ```

   `-Installer` runs Inno Setup for you if `iscc` is on PATH. Otherwise build manually:

2. Install Inno Setup 6.1+ (`choco install innosetup`), then:

   ```powershell
   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
       /DMyAppVersion=1.0.0 `
       /DMySourceDir="C:\DaxAlgoBuild\DaxAlgo-Terminal" `
       installer\DaxAlgoTerminal.iss
   ```

**Output location:** local builds write all artifacts under **`C:\DaxAlgoBuild\`** by default
(`DaxAlgo-Terminal\` published app, `installer\DaxAlgo-Terminal-Setup-vX.Y.Z.exe`), keeping build
output off the code-only repo drive. Override with `publish.ps1 -Output <path>` or `/DMyOutputDir=`
+ `/DMySourceDir=` on `ISCC`. The CI release workflow overrides this back to the runner workspace.

The CI **Release** workflow does all of this automatically on a `vX.Y.Z` tag and attaches the
setup `.exe` (alongside the portable zip) to the draft GitHub Release.

## Silent install

```powershell
DaxAlgo-Terminal-Setup-v1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="installwebview2,installdocker"
```

Omit a task name to skip that dependency.
