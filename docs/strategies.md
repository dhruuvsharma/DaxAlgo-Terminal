# Strategies

> Last updated: 2026-05-25

The terminal ships 20+ canonical strategies behind one `IBacktestStrategy` plug-in seam. Each strategy has two halves:

- **Engine side** (`IBacktestStrategy`) — pure tick-driven logic; runs in both the backtest CLI and the Tools → Backtest tab. Lives in `Infrastructure/Backtest/Strategies/`.
- **Live UI side** — a `MetroWindow` + view-model that wraps the engine impl, picks an instrument, lets you set parameters, and surfaces signals as notifications. Lives in `src/TradingTerminal.Strategies.<Name>/`.

The split means the same logic powers backtest sweeps and live signal mode without duplication.

## Strategy catalog

| Family | Strategy id | What it does |
|---|---|---|
| Demo | `buyAndHold` | Market-buy on the first tick, sell on the last. Engine smoke-test. |
| Demo | `meanReversion` | Rolling-mean reversion with fixed thresholds. |
| Demo | `donchianBreakout` | N-tick Donchian channel break, trailing-mid stop. |
| HFT | `avellanedaStoikov` | Avellaneda-Stoikov optimal market maker — inventory-shifted reservation, online variance EMA, configurable requote cadence. |
| HFT | `microprice` | Size-weighted microprice deviation scalper. |
| HFT | `ornsteinUhlenbeck` | Online AR(1)-fit OU process, z-score entry/exit bands. |
| HFT | `twap` | TWAP parent-order slicer with tail flush. |
| Forex | `bollinger` | Bollinger band reversion (Bollinger 2001). |
| Forex | `maCrossover` | Fast/slow SMA cross — golden / death cross (Murphy 1999). |
| Forex | `rsi2` | Connors RSI(2) reversion (Connors 2008). |
| Forex | `londonOpen` | Asian-range / London-open breakout with ATR trail (Volman 2011). |
| Forex | `macd` | 12/26/9 MACD signal-line crossover (Appel 2005). |
| Index | `trendFilter` | Long when price > 200-period SMA, else flat (Faber 2007). |
| Index | `volTarget` | Position sized to `target_vol / realized_vol_ewma` (AQR risk-parity overlay). |
| Index | `gapFade` | Detect overnight gap, fade toward previous close. |
| Index | `eodMomentum` | Take direction of day's open-to-now return in the last N% of the UTC session (Gao-Han-Li-Zhou 2018). |
| Index | `pullback` | Trend filter + N-tick pullback + resumption entry — "buy the dip" with a percentage stop and target. |
| L2 / DOM | `bookPressure` | Cumulative order-book imbalance signal (Cartea-Jaimungal-Penalva). Trades touch sizes today; generalises to `Microstructure.CumulativeImbalance` over a `DepthSnapshot` when L2 ticks land. |
| L2 / DOM | `liquiditySweep` | Aggressive-flow / sweep detector — rolling-mean depth + same-side price drop. |
| L2 / DOM | `iceberg` | Hidden-liquidity sticky-touch heuristic; trades toward the iceberg-supported side. |
| L2 / DOM | `vpin` | VPIN-style order-flow toxicity (Easley, López de Prado, O'Hara 2012). Mean-reverts against high toxicity. |
| L2 / DOM | `thinBook` | Breakout entry gated by a depth threshold — passes on thin-book setups. |

Plus two ML-oriented strategies:

| Family | Project | What it does |
|---|---|---|
| ML | `TradingTerminal.Strategies.OnlineRegressionAlpha` | Online recursive-least-squares fit; trades the residual sign. |
| ML | `TradingTerminal.Strategies.AnomalyDetector` | Rolling z-score anomaly detector. |

These are **textbook reference implementations, not curve-fit production systems**. Their PnL is regime-dependent, especially on the demo synthetic dataset. Pair them with real broker tick data through the same parquet pipeline to evaluate seriously.

## Adding a new strategy

Each strategy is its own project (`src/TradingTerminal.Strategies.<Name>/`) following the same six-file shape as the others. The fastest path is to copy an existing project, rename, and edit.

1. **Copy** the closest existing project under `src/`. Rename the directory, the `.csproj`, and the per-strategy class prefix (e.g. `BollingerStrategy*` → `MyStrategy*`).
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
