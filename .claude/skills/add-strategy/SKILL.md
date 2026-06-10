---
name: add-strategy
description: Recipe for adding a new strategy to DaxAlgo Terminal — both the engine-side IBacktestStrategy implementation and the per-strategy live UI project that wraps it via LiveSignalStrategyViewModelBase. Use when the user asks "add a strategy", "new strategy", "implement strategy X", or references IBacktestStrategy. Covers project layout, DI registration, CLI registration, indicator reuse, and the catalog wiring for the Backtest tab.
---

# Add a Strategy

Strategies are plug-ins. The shell never references strategy concretes — they're discovered via `IStrategyFactory` and registered in DI. There are TWO surfaces:

1. **Engine-side** (`IBacktestStrategy`) — runs in both backtest CLI and the Tools → Backtest tab. Lives in `Infrastructure/Backtest/Strategies/`.
2. **Live UI** (`LiveSignalStrategyViewModelBase`) — its own `TradingTerminal.Strategies.<Name>` project that wraps the engine-side strategy.

Most strategies need both. Some (HFT/MM) may only ship as engine-side.

**HARD NAMING RULE — no exceptions.** If it registers an `ITradingStrategy` / `StrategyFactoryRegistration` (i.e. it appears in the shell's Strategies pane), it is a strategy project: `src/TradingTerminal.Strategies.<Name>/`, namespace `TradingTerminal.Strategies.<Name>`, **Strategies** solution folder, DI file `DependencyInjection.cs` exposing `Add<Name>Strategy()`, registered from `AddStrategyPlugins()` in `AppDependencyInjection.cs` — NOT from the tool blocks in `App.xaml.cs`. This applies even to monitor/screener-style strategies with no per-instrument signal loop (e.g. OrderFlowPressureMap, a multi-ticker heatmap) and even when the VM doesn't inherit `LiveSignalStrategyViewModelBase`. `TradingTerminal.<Name>` + `Add…Surface` is reserved for non-strategy tool windows. (This rule exists because OrderFlowPressureMap was once misbuilt as a tool project and had to be renamed.)

## Engine-side (IBacktestStrategy) recipe

1. **New file** `src/TradingTerminal.Infrastructure/Backtest/Strategies/<Name>Strategy.cs`.
2. **Implement `IBacktestStrategy`** — `OnStartAsync` / `OnTickAsync(Tick)` / `OnTradeAsync(TradePrint)` (default no-op; override for trade-tape strategies) / `OnOrderEventAsync` / `OnEndAsync`. Place orders through `IOrderRouter`, never through `IBrokerClient`.
3. **Reuse helpers** — `Core/MarketData/Indicators.cs` (SMA/EMA/RSI/ATR/stdev), `Core/MarketData/Microstructure.cs` (microprice, QI, half-spread, `ClassifyAggressor` for Lee-Ready). Don't roll your own.
4. **Register in catalog**:
   - UI dropdown: `BacktestStrategyCatalog.cs`.
   - CLI: `ResolveStrategy` in `src/TradingTerminal.Backtest.Cli/Program.cs`.
5. **Optional**: parameter sweep grid in CLI — add a `BuildXxxGrid` method for parallel exploration via the `sweep` subcommand.
6. **Trade-tape strategies** — backtest replay interleaves quotes + trades via `BacktestEvent` + a k-way merge in `BacktestTickSource`. The strategy sees them in event-time order; don't add a separate clock.

## Live UI strategy recipe

1. **New project** `src/TradingTerminal.Strategies.<Name>/` — mirror an existing one (`TradingTerminal.Strategies.OrnsteinUhlenbeck` is a clean single-signal template; `CumulativeDelta` for a trade-tape one).
2. **Add to solution** with `dotnet sln add` **and verify the .sln actually changed**. If the command prints a "Solution folder X already contains a project" warning, it silently aborted — fall back to editing `.sln` by hand (Project block + 12-line ProjectConfigurationPlatforms set + NestedProjects mapping; copy the GUID pattern from any existing strategy and generate a new project GUID via PowerShell `[System.Guid]::NewGuid()`). Reference `Core`, `Infrastructure`, `UI`.
3. **Metadata class** `<Name>Strategy.cs` in the live project implementing `ITradingStrategy` (`Id`, `DisplayName`, `Description`). **This is what populates the Strategies pane** via `IStrategyFactory.All` — without it the strategy is invisible to the shell even though the engine-side and `StrategyFactoryRegistration` exist. The engine-side `<Name>Strategy.cs` in `Infrastructure/Backtest/Strategies/` is a SEPARATE class with the same name in a different namespace — alias the engine class in the live VM (`using EngineStrategy = TradingTerminal.Infrastructure.Backtest.Strategies.<Name>Strategy;`) to avoid ambiguity when calling it from `BuildStrategy`. Some strategies give the engine class a distinct name (e.g. a `*ReversionStrategy` suffix) to dodge this — either approach is fine.
4. **View-model** inherits `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`). Its ctor takes a `LiveStrategyHostServices` bundle (Repository + Hub + Ingest + Store + BrokerSelector) — do NOT add new ad-hoc deps; route them through DI elsewhere.
5. **MetroWindow shell** — open as its own window (the established convention across all 16 shipped strategies).
6. **DI registration** — your `Add<Name>Strategy()` extension must register THREE things: `AddSingleton<ITradingStrategy, <Name>Strategy>()` (metadata, drives the Strategies pane), `AddTransient<<Name>ViewModel>()`, `AddTransient<<Name>Window>()`, plus a `StrategyFactoryRegistration` singleton mapping the StrategyId to the view+vm factory pair. Then call `services.Add<Name>Strategy()` once in `AppDependencyInjection.AddStrategyPlugins` and add the project reference to `TradingTerminal.App.csproj`. Don't edit anything else in the shell.
7. **Hub subscription** — the base class subscribes to `IMarketDataHub.Quotes(InstrumentId)`. For trade-tape strategies, also subscribe to `IMarketDataIngest.SubscribeTrades(...)` and gate Continue on `BrokerSupportsTradeTape(broker)` — today only IB returns true. See [regime-cube-strategy](../regime-cube-strategy/SKILL.md) for the standard shape.
8. **Warm-up** — `LiveSignalStrategyViewModelBase` reads 1-minute bars from `IMarketDataStore.GetRecentBarsAsync` on Start (granularity intentionally differs from the 15s live aggregation — store has no sub-minute bars and 1m context is better than none).

## Strategy library reference (textbook reference implementations)

Families currently shipped — read one before writing a new one:

- **HFT/microstructure**: Avellaneda-Stoikov MM, Ornstein-Uhlenbeck.
- **Index baselines**: vol targeting, pullback continuation.
- **L2 / depth-of-market**: book pressure, liquidity sweep, iceberg detection, VPIN-style toxicity, thin-book filter, cumulative delta, Apex Scalper (composite MT5 port).
- **ML / AI**: online regression alpha.
- **Regime cubes / surfaces** (Helix Toolkit 3D): Order Flow Cube, Order Flow Surface Spike, Imbalance Heat Front, Index K-Score Surface. See [regime-cube-strategy](../regime-cube-strategy/SKILL.md) before adding more from `ideas.md`.

(The forex/index/microprice/TWAP/anomaly baselines were removed in the data-only-signals refactor — don't reference them.)

These are textbook implementations, not curve-fit. Stay regime-dependent.

## Hard rules

- **No `new`-ing strategies from the shell.** Always through `IStrategyFactory` / DI.
- **No business logic in view-model `.xaml.cs`.** Strict MVVM.
- **Orders go through `IOrderRouter`**, not `IBrokerClient`. Live uses `LiveOrderRouter`, backtest uses `BacktestOrderRouter`.
- **Reuse `Indicators` and `Microstructure`** — don't reimplement SMA/EMA/RSI/microprice.

## Reference reads

- `src/TradingTerminal.Infrastructure/Backtest/Strategies/RsiStrategy.cs` — clean engine-side template.
- `src/TradingTerminal.Strategies.Rsi/` — clean live-UI template (RSI-shaped, the canonical one).
- `src/TradingTerminal.Core/Backtest/IBacktestStrategy.cs` — engine seam contract.

See also: [backtest-engine](../backtest-engine/SKILL.md) for fee/risk model wiring.
