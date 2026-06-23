# DaxAlgo Terminal — Linux / Raspberry Pi port

This folder is the home for everything Linux-specific: build/run tooling, the Docker
image, helper scripts, and (in Phase 1) the cross-platform **Avalonia** UI that replaces
the Windows-only WPF shell.

> Branch: `linux-port-phase0`. The Windows `main` branch is never touched by this work.

## Status

**Phase 0 — headless Linux build (DONE).** The non-UI layer now builds, tests, and runs
on Linux (x64 + ARM64/Raspberry Pi), while the Windows WPF build stays 100% intact.

| Project | Linux (`net9.0`) | Windows (`net9.0-windows7.0`) |
|---|---|---|
| `TradingTerminal.Core` | ✅ | ✅ |
| `TradingTerminal.MarketData` | ✅ | ✅ |
| `TradingTerminal.Infrastructure` | ✅ (WPF-free) | ✅ |
| `TradingTerminal.Backtest.Engine` | ✅ | ✅ |
| `TradingTerminal.Backtest.Cli` (`daxalgo-backtest`) | ✅ | ✅ |
| `TradingTerminal.UI` / `App` / `Login` / `Ai*` / `Ml*` / `Strategies.*` / tool windows | ❌ WPF — Windows only (Phase 1: Avalonia) | ✅ |

### How it works (no separate codebase — one source, two targets)

The headless projects **multi-target** `net9.0;net9.0-windows7.0`. The Linux leg compiles
the exact same source minus the Windows bits, guarded by `#if WINDOWS`:

- `WpfDispatcher` (WPF) is `#if WINDOWS`; on Linux `ImmediateUiDispatcher` (in
  `TradingTerminal.MarketData/Threading/`) is registered for `IUiDispatcher`.
- DPAPI (`AiAnalystOptions`) is `#if WINDOWS`; Linux uses a base64 fallback
  (TODO: back with the OS keyring — libsecret/Keychain).
- The Windows Job Object sidecar guard is inert off-Windows; teardown falls back to
  `Process.Kill(entireProcessTree: true)`.
- `Directory.Build.props` sets `EnableWindowsTargeting=true` so Linux can restore the
  Windows leg's reference packs (only the `net9.0` leg actually compiles off-Windows).
- NinjaTrader (`NTDirect.dll`) and IB (`CSharpAPI.dll`) are path-gated (`HAS_NTAPI` /
  `HAS_IBAPI`) and simply compile out on Linux.

## Two UI versions — what runs where

There are **two UI heads** on one shared core. The Avalonia app is **cross-platform — it runs on
Windows too**, not just Linux.

| What | Windows | Linux / Raspberry Pi |
|---|---|---|
| **WPF app** (`src/TradingTerminal.App`) — the rich Windows shell | ✅ runs | ❌ not supported (WPF) |
| **Avalonia app** (`src/linux/TradingTerminal.App.Avalonia`) — cross-platform shell | ✅ runs | ✅ runs |
| **Backtest CLI** (`src/shared/TradingTerminal.Backtest.Cli`) | ✅ | ✅ |
| **Headless tests** (`tests/TradingTerminal.Tests.Headless`) | ✅ | ✅ |
| **Windows-only tests** (`tests/TradingTerminal.Tests`, WPF/DuckDB/AI) | ✅ | ❌ |
| Full solution build (`dotnet build`) | ✅ everything | ⚠️ WPF projects don't build on Linux — build the `src/shared` + `src/linux` projects individually |

## Run / build / test

**Run an app** (from the repo root):

```bash
dotnet run --project src/TradingTerminal.App                  # WPF shell      (Windows only)
dotnet run --project src/linux/TradingTerminal.App.Avalonia   # Avalonia shell (Windows, Linux, Pi)
dotnet run --project src/shared/TradingTerminal.Backtest.Cli -- --help   # headless CLI (both)
```

In Visual Studio, set either app as the **startup project** (Solution Explorer → right-click →
Set as Startup Project) and pick a profile from the run dropdown — WPF profiles: *App (Login) /
Dev: Simulated / Dev: Replay / Dev: Live*; Avalonia profiles: *Avalonia Shell (Desktop) /
(Dev: Simulated)*.

**Build + test, one shot:**

```bash
# Windows  — full solution (both UIs) + both test suites
pwsh ./build-and-test.ps1

# Linux/Pi — headless + Avalonia build, headless tests, CLI smoke, arm64 restore probe
linux/build-and-test.sh
```

**Linux build, project-by-project** (the WPF projects are skipped on Linux):

```bash
dotnet build src/shared/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -f net9.0
dotnet build src/linux/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj
dotnet test  tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -f net9.0
```

### Docker (reproducible Linux build, no local SDK needed)

```bash
docker build -f linux/Dockerfile -t daxalgo-linux .   # build + test + arm64 probe in one image
```

> ⚠️ On **Docker Desktop for Windows**, do **not** bind-mount the repo (`-v`) for builds — NuGet
> restore over the filesystem bridge is pathologically slow and hangs. Use `docker build` (it
> `COPY`s the source onto the fast container fs). Bind-mounts are fine on a native Linux host.

### Raspberry Pi (ARM64)

Packages restore for `linux-arm64` (verified). On the Pi, install the .NET 9 SDK/runtime and run
the commands above; the Avalonia shell renders via Skia. Chart-heavy/real-time windows will be
marginal on Pi hardware.

## Phase 1 — Avalonia UI (IN PROGRESS)

The WPF shell + 66 XAML views are Windows-only. Phase 1 ports them to **Avalonia** (Skia,
runs on Pi). Cross-platform UI projects live under `src/linux/`.

**Repo layout (platform split):** `src/shared/` = portable, cross-platform projects (Core,
MarketData, Infrastructure, Backtest.Engine, Backtest.Cli — and the future portable VM layer);
`src/linux/` = Avalonia UI head; `src/web/` = future web head; the WPF projects stay flat under
`src/` for now (grouped under the **Windows** solution folder), relocating to `src/windows/` as
they're touched.

**Foundation done:** `src/linux/TradingTerminal.App.Avalonia` — a cross-platform Avalonia desktop
shell (`net9.0`) on top of the portable core. **Builds on Windows and Linux** (verified in the
Docker image). It proves the stack: `ViewModelBase` is plain `CommunityToolkit.Mvvm`
`ObservableObject` (portable), and only the WPF *views* (`*.xaml.cs`) are Windows-coupled — so
the porting pattern is **copy the WPF view → Avalonia AXAML, reuse the VM unchanged**.

**Next:** extract/share the portable VM layer (so the Strategies.* / tool VMs are reused as-is),
then port views window-by-window (catalog + Activity Log first). Hard blockers to rework or
feature-flag on Linux: WebView2 (Charts), HelixToolkit (3D regime cubes), NinjaTrader broker.

Run the shell locally (needs a display; on Linux use X11/Wayland, on Pi the desktop):

```bash
dotnet run --project src/linux/TradingTerminal.App.Avalonia
```
