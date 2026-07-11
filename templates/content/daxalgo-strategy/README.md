# DaxNewStrategy

A **DaxAlgo Terminal strategy plugin**, scaffolded by `dotnet new daxalgo-strategy`. It ships an
EMA-cross demo kernel — replace the math in `DaxNewStrategy/Engine/`, keep the shape.

> Scaffolded **headless** (backtest-only). Pass `--ui` (`dotnet new daxalgo-strategy -n MyStrategy --ui`)
> to also get a live strategy **window** — a view-model on `LiveSignalStrategyViewModelBase` + a
> `MetroWindow` view + the `StrategyFactoryRegistration` — running the same kernel. A UI plugin
> references `DaxAlgo.Sdk.Wpf` instead of `DaxAlgo.Sdk`.

## The loop

```powershell
dotnet build DaxNewStrategy.slnx    # 1. build plugin + tests
dotnet test  DaxNewStrategy.slnx    # 2. offline kernel harness (no broker, no data files)
./pack-plugin.ps1                   # 3. -> DaxNewStrategy.daxplugin
```

4. In DaxAlgo Terminal: **Plugins → Manage strategy plugins… → Install plugin…**, pick the
   `.daxplugin`, restart. The strategy appears in Backtest Studio (and the `daxalgo-backtest` CLI)
   under the id in `plugin.json`. If it fails to load, the Plugin Manager shows the classified
   reason and the header chip lights up.

## Layout

| Path | What |
|---|---|
| `DaxNewStrategy/Engine/DaxNewStrategyKernel.cs` | The strategy — `IBacktestStrategy` signal logic |
| `DaxNewStrategy/DaxNewStrategyPlugin.cs` | `IStrategyPlugin` entry point + catalog descriptor |
| `DaxNewStrategy/plugin.json` | Manifest the host reads before loading any code |
| `DaxNewStrategy/DaxNewStrategyViewModel.cs` | *(`--ui` only)* live window VM on `LiveSignalStrategyViewModelBase` |
| `DaxNewStrategy/DaxNewStrategyWindow.xaml`(`.cs`) | *(`--ui` only)* the `MetroWindow` view |
| `DaxNewStrategy.Tests/` | Offline harness (synthetic ticks → recording router) |
| `pack-plugin.ps1` | Builds + packages the integrity-indexed `.daxplugin` |
| `CLAUDE.md` / `AGENTS.md` | Context pack for AI coding agents (Claude Code, Codex, Cursor, …) |

## AI-assisted authoring

Open this folder in your agent of choice — `CLAUDE.md`/`AGENTS.md` carry the contracts, hard
rules, and commands, so "make it trade a Bollinger-band mean reversion instead" is a one-prompt
change with the tests as the guard rail.

## Rules that matter

- The SDK is **pre-1.0**: the host requires an exact major.minor match. When the terminal updates
  its SDK, bump the `DaxAlgo.Sdk` PackageReference + `plugin.json`'s `targetSdkVersion` and rebuild.
- Never reference `TradingTerminal.*` projects directly or ship host DLLs in your package — the
  host provides them; the `ExcludeAssets="runtime"` on the SDK reference keeps your output clean.
- No file/network/process access in strategy code — install-time policy scanning flags it, and a
  curated marketplace review rejects it.

Docs: <https://github.com/dhruuvsharma/DaxAlgo-Terminal/blob/main/docs/plugins.md>
