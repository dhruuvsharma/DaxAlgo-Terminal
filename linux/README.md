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

## Build / test / run on Linux

From the repo root:

```bash
# Build the headless layer
dotnet build src/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -f net9.0
dotnet build src/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj   -f net9.0

# Run the headless test suite
dotnet test tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -f net9.0

# Run a backtest on Linux
dotnet run --project src/TradingTerminal.Backtest.Cli -f net9.0 -- synth --output /tmp/ticks.parquet --ticks 3000
dotnet run --project src/TradingTerminal.Backtest.Cli -f net9.0 -- run --strategy meanReversion --symbol TEST --source parquet --data /tmp/ticks.parquet --output /tmp/bt
```

Or use the helper scripts in this folder (run from anywhere):

```bash
linux/build-and-test.sh      # clean build + headless tests + CLI smoke + arm64 restore probe
```

### Docker (reproducible, no local SDK needed)

```bash
# One-shot CI-style build+test image
docker build -f linux/Dockerfile -t daxalgo-linux .

# Or bind-mount the repo into the SDK image for a dev loop
docker run --rm -v "$PWD":/work -w /work mcr.microsoft.com/dotnet/sdk:9.0 linux/build-and-test.sh
```

### Raspberry Pi (ARM64)

The packages restore for `linux-arm64` (verified). On the Pi, install the .NET 9 SDK/runtime
and run the same commands above. Chart-heavy/real-time work will be marginal on Pi hardware.

## Roadmap — Phase 1+ (Avalonia UI)

The WPF shell + 66 XAML views are Windows-only. Phase 1 ports them to **Avalonia** (Skia,
runs on Pi). Cross-platform UI projects derived from the WPF originals will live under `src/`
with an `.Avalonia` suffix (or a dedicated grouping) — copy the WPF view/VM, swap the XAML
namespaces + controls, and reuse the `CommunityToolkit.Mvvm` view-models unchanged. Hard
blockers to rework or feature-flag on Linux: WebView2 (Charts), HelixToolkit (3D regime
cubes), NinjaTrader broker.
