---
name: add-strategy
description: Recipe for adding a new strategy to DaxAlgo Terminal — both the engine-side IBacktestStrategy implementation and the per-strategy live UI project that wraps it via LiveSignalStrategyViewModelBase. Use when the user asks "add a strategy", "new strategy", "implement strategy X", or references IBacktestStrategy. Covers project layout, DI registration, CLI registration, indicator reuse, and the catalog wiring for the Backtest tab.
---

# Add a Strategy

Strategies are plug-ins. The shell never references strategy concretes — they're discovered via `IStrategyFactory` and registered in DI. There are TWO surfaces:

1. **Engine-side** (`IBacktestStrategy`) — runs in both backtest CLI and the Tools → Backtest tab. Lives in `Infrastructure/Backtest/Strategies/`.
2. **Live UI** (`LiveSignalStrategyViewModelBase`) — its own `TradingTerminal.Strategies.<Name>` project that wraps the engine-side strategy.

Most strategies need both. Some (HFT/MM) may only ship as engine-side.

## Engine-side (IBacktestStrategy) recipe

1. **New file** `src/TradingTerminal.Infrastructure/Backtest/Strategies/<Name>Strategy.cs`.
2. **Implement `IBacktestStrategy`** — `OnStart` / `OnTick` / `OnOrderEvent` / `OnEnd`. Place orders through `IOrderRouter`, never through `IBrokerClient`.
3. **Reuse helpers** — `Core/MarketData/Indicators.cs` (SMA/EMA/RSI/ATR/stdev), `Core/MarketData/Microstructure.cs` (microprice, QI, half-spread). Don't roll your own.
4. **Register in catalog**:
   - UI dropdown: `BacktestStrategyCatalog.cs`.
   - CLI: `ResolveStrategy` in `src/TradingTerminal.Backtest.Cli/Program.cs`.
5. **Optional**: parameter sweep grid in CLI — add a `BuildXxxGrid` method for parallel exploration via the `sweep` subcommand.

## Live UI strategy recipe

1. **New project** `src/TradingTerminal.Strategies.<Name>/` — mirror an existing one (`TradingTerminal.Strategies.Rsi` is a clean RSI-shaped template).
2. **Add to solution** with `dotnet sln add`. Reference `Core`, `Infrastructure`, `UI`.
3. **View-model** inherits `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`). Wraps the engine-side `IBacktestStrategy` for live execution.
4. **MetroWindow shell** — open as its own window (see existing 21-strategy convention; commit `fc521de`).
5. **DI registration**:
   ```csharp
   services.Add<Name>Strategy();  // extension method
   ```
   Add one line to `App.xaml.cs`. Don't edit anything else in the shell.

## Strategy library reference (textbook reference implementations)

Five families, all already shipped — read one before writing a new one:

- **HFT/microstructure**: Avellaneda-Stoikov MM, Microprice, Ornstein-Uhlenbeck, TWAP execution.
- **FX baselines**: Bollinger, MA crossover, Connors RSI(2), London-open breakout, MACD.
- **Index baselines**: 200-SMA trend filter, vol targeting, gap fade, end-of-day momentum, pullback continuation.
- **L2 / depth-of-market**: book pressure, liquidity sweep, iceberg detection, VPIN-style toxicity, thin-book filter.
- **Demo**: Buy&Hold, MeanReversion, Donchian.

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
