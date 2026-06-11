# Strategies

> Last updated: 2026-05-31

The terminal ships 9 live strategies behind one `IBacktestStrategy` plug-in seam (plus buy-and-hold / mean-reversion / Donchian engine demos). Each strategy has two halves:

- **Engine side** (`IBacktestStrategy`) — pure tick-driven logic; runs in both the backtest CLI and the Tools → Backtest tab. Lives in `Infrastructure/Backtest/Strategies/`.
- **Live UI side** — a `MetroWindow` + view-model that wraps the engine impl, picks an instrument, lets you set parameters, and surfaces signals as notifications. Lives in `src/TradingTerminal.Strategies.<Name>/`.

The split means the same logic powers backtest sweeps and live signal mode without duplication.

## Screenshots

| Strategy | Window | Settings |
|---|---|---|
| APEX microstructure scalper | ![APEX scalper](../images/apexmicrostructurescalperwindow.png) | ![APEX settings](../images/apexmicrostructurescalpersettings.png) |
| Cumulative delta scalper | ![Cumulative delta](../images/cumulativedeltascalperwindow.png) | ![Settings 1](../images/cumulativedeltascalpersettings_1.png) ![Settings 2](../images/cumulativedeltascalpersettings_2.png) |
| Imbalance Heat Front (3D) | ![Imbalance Heat Front](../images/imbalanceheatfrontwindow.png) | ![Settings](../images/imbalanceheatfrontsettings.png) |
| Index K-Score Surface (3D) | ![Index K-Score Surface](../images/indexkscoresurfacewindow.png) | ![Settings](../images/indexkscoresurfacesettings.png) |

## Strategy catalog

| Family | Strategy id | What it does |
|---|---|---|
| Demo | `buyAndHold` | Market-buy on the first tick, sell on the last. Engine smoke-test. |
| Demo | `meanReversion` | Rolling-mean reversion with fixed thresholds. |
| Demo | `donchianBreakout` | N-tick Donchian channel break, trailing-mid stop. |
| HFT | `ornsteinUhlenbeck` | Online AR(1)-fit OU process, z-score entry/exit bands. |
| Index | `volTarget` | Position sized to `target_vol / realized_vol_ewma` (AQR risk-parity overlay). |
| L2 / DOM | `vpin` | VPIN-style order-flow toxicity (Easley, López de Prado, O'Hara 2012). |
| Regime cube (3D) | `orderFlowCube` | Order-flow regime cube — CVD × aggressor × size, Helix 3D scatter + trail. |
| Regime cube (3D) | `orderFlowSurfaceSpike` | Z-score spike detector over a slice × price-bin matrix surface. |
| Regime cube (3D) | `imbalanceHeatFront` | L2 bid/ask pressure surface with mirror-book detection. |
| Regime cube (3D) | `indexKScoreSurface` | Per-component K-score surface for index baskets. |
| Composite | `apexScalper` | APEX microstructure scalper v2 — tape-primary 11-signal composite (Σ⁻¹·IC weights, isotonic g(C) entry gate, first-passage EV exits, ¼-Kelly sizing). Full math in the [project README](../src/TradingTerminal.Strategies.ApexScalper/README.md). |
| Monitor | `orderflow.pressuremap` | 1-Minute Order Flow Pressure Map — S&P 100/500 ticker × time heatmap flagging unusual 1m volume and absorption vs. breakthrough. Live-only window (`TradingTerminal.Strategies.OrderFlowPressureMap`, no backtest id). |

The same engine ids are selectable in the Backtest tab and the `daxalgo-backtest` CLI. **Cumulative delta** ships as a live-only window (`TradingTerminal.Strategies.CumulativeDelta`, no backtest id).

These are **textbook reference implementations, not curve-fit production systems**. Their PnL is regime-dependent, especially on the demo synthetic dataset. Pair them with real broker tick data through the same parquet pipeline to evaluate seriously.

## Adding a new strategy

Each strategy is its own project (`src/TradingTerminal.Strategies.<Name>/`) following the same six-file shape as the others. The fastest path is to copy an existing project, rename, and edit.

1. **Copy** the closest existing project under `src/`. Rename the directory, the `.csproj`, and the per-strategy class prefix (e.g. `VolatilityTargetedStrategy*` → `MyStrategy*`).
2. **Files in the new project:**
   - `MyStrategy.cs` — `ITradingStrategy` descriptor with `Id` / `DisplayName` / `Description`.
   - `MyStrategyViewModel.cs` — extends `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`). Declare your parameters as `[ObservableProperty]`s, override `BuildStrategy(Contract contract)` to return your engine-side `IBacktestStrategy` impl.
   - `MyStrategyWindow.xaml(.cs)` — a `MetroWindow`. Surface parameter inputs, controls, and charts however you want.
   - `DependencyInjection.cs` — one `AddMyStrategy()` extension that registers the descriptor, VM, view, and a `StrategyFactoryRegistration`.
3. Add a `<ProjectReference>` to your new project in `TradingTerminal.App.csproj`.
4. Add `services.AddMyStrategy();` to `AppDependencyInjection.AddStrategyPlugins()`.
5. Add the project entry to `TradingTerminal.sln`.

The new strategy shows up in the left pane on next launch and opens in its own window.

### Where to put the engine-side logic

If your strategy can be backtested (almost always — even live-only strategies benefit from offline replay), put the actual signal logic in `src/TradingTerminal.Infrastructure/Backtest/Strategies/<Name>Strategy.cs` implementing `IBacktestStrategy`. The live VM then constructs that same class inside `BuildStrategy(contract)`. This is what every shipped strategy does.

Register the engine impl in `BacktestStrategyCatalog` (for the UI dropdown) and `ResolveStrategy` (for the CLI). The shared `Indicators` (`Core/MarketData/Indicators.cs`) and `Microstructure` (`Core/MarketData/Microstructure.cs`) modules cover SMA, EMA, Wilder RSI, ATR, rolling stdev, microprice, queue imbalance, half-spread, cumulative imbalance, weighted mid, side depth, estimated slippage, and largest level gap — there is rarely a reason to roll your own.

### Generator for the boilerplate

If you're scaffolding many strategies at once, edit the manifest in `scripts/gen-strategy-projects.ps1` and re-run it. The script overwrites the generated files in each project, so customise *new* files in those projects (or fork the generator) rather than editing the ones it produces.

## Tuning a strategy's parameters

Each per-strategy project's `<Name>StrategyViewModel.cs` declares parameters as `[ObservableProperty]`s with defaults from the engine implementation's constructor. Edit the defaults in the VM, OR — more commonly — change them at runtime in the strategy window's Parameters panel before hitting Start.

For backtest parameter sweeps, use the CLI's `sweep` subcommand — see [backtesting.md](backtesting.md).

## Where the live VM hooks into shared plumbing

`LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`) handles the boilerplate: instrument picker via `SignalInstrumentCatalog`, tick subscription via `IMarketDataRepository` (or `IMarketDataHub` for new strategies), router lifecycle, `SignalEntry` grid wiring, and `INotificationPublisher` integration. Your subclass only declares parameters and constructs the engine. Read its source to see what the base class already gives you before adding fields.
