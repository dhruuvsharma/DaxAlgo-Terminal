---
name: regime-cube-strategy
description: Recipe + conventions for the regime-cube strategy family (3-axis or surface-based, with Helix Toolkit 3D viz) from ideas.md — Order Flow Cube and Order Flow Surface Spike already shipped end-to-end; Microstructure Liquidity / Vol-of-Vol / Cross-Asset Risk / Auction Market Theory queued. Use when the user asks to add a strategy from ideas.md, references "regime cube", "3D scatter", "Helix Toolkit", "Order Flow Cube" / "Surface Spike", or asks for the cube #3+ build.
---

# Regime Cube Strategy

The cube family lives in `D:\Github\DaxAlgo Terminal\ideas.md`. Five strategies, each with three orthogonal signals → octants of a "regime cube". All work on spot instruments only (no options, no futures curve, no expiry).

## Build order (per ideas.md)

1. **Order Flow Cube** — **SHIPPED** (`orderflow.cube`).
2. **Order Flow Surface Spike** — **SHIPPED** (`orderflow.surface.spike`). Not in the original ideas.md list, added as a sibling.
3. **Auction Market Theory Cube** — next; tick-data only, profile-based.
4. **Vol-of-Vol Regime Cube** — close prices only; easiest to backtest.
5. **Cross-Asset Risk Cube** — multi-instrument plumbing; reuse [[project-market-regime]] for the SPY/VIX/DXY composite.
6. **Microstructure Liquidity Cube** — needs L2 depth wiring; cTrader ready, IB/NT need work.

## The rule that applies to every cube

> Build the **2D-with-color** version first (two axes as X/Y, third as point color or size, with a fading trail of the last N bars). Validate edge in backtest before adding true 3D rendering.

3D scatter plots consistently produce worse decisions than 2D-plus-color equivalents due to occlusion and depth ambiguity. The edge is in the signal definitions, not the projection.

(Order Flow Cube and Order Flow Surface Spike ship with 3D anyway because the user wanted to see Helix Toolkit in action. From cube #3 forward, ship 2D-with-color first; only add 3D once edge is proven.)

## Project layout

A cube strategy ships as TWO things (matches [add-strategy](../add-strategy/SKILL.md)):

1. **Engine** `Infrastructure/Backtest/Strategies/<Name>Strategy.cs` — implements `IBacktestStrategy`. May override `OnTradeAsync(TradePrint)` if it consumes trade tape.
2. **Live UI project** `src/TradingTerminal.Strategies.<Name>/`:
   - `<Name>Calculator.cs` — pure math, no UI deps. Computes the three signals; for cubes, expose them as a single record/state struct.
   - `<Name>ViewModel.cs` — extends `LiveSignalStrategyViewModelBase`, takes `LiveStrategyHostServices`. Hosts the log panel, capability check, and viz state.
   - `<Name>Window.xaml` + `.xaml.cs` — MetroWindow shell. 3D viewport via Helix Toolkit if applicable.
   - `DependencyInjection.cs` — single `Add<Name>Strategy()` extension method.
   - `InstrumentCatalog.cs` — same instrument-discovery shape every cube uses.
   - `QuoteDerivedTradeSynthesizer.cs` — only when the broker has no trade tape; synthesizes prints from quote midpoint crossings. Use sparingly — real tape is preferable.

## 3D visualization conventions

- **HelixToolkit.Wpf 2.27.0** — only WPF 3D library with depth control + lighting + rotation/zoom out of the box. NU1701 warning is expected (targets .NET Framework, works on net9.0-windows via shim). Don't switch to ScottPlot 3D — it doesn't exist; ScottPlot is 2D-only.
- **Scatter cube** (Order Flow Cube shape): sphere markers (color = third axis), axes drawn explicitly, threshold tick markers on each axis, unit-cube wireframe. Trail = last N points fading in alpha.
- **Heightmap surface** (Surface Spike shape): `MeshGeometry3D` indexed grid (rows × cols), height = signal magnitude × scale, single `LinearGradientBrush` horizontal heatmap (blue at −Zclamp, gray at 0, red at +Zclamp). Spike cell highlighted with a sphere overlay.
- **Refresh cadence**: rebuild on every tick. Verts are typically <2000; perf is trivial.

## Shared trade-tape conventions (codify for cube #3+)

These are project-wide rules for any strategy that consumes `IBrokerClient.SubscribeTradesAsync`:

- **Capability check at Continue.** Each VM has a static `BrokerSupportsTradeTape(broker)` lookup. Fail loudly (do NOT silently fall back to quote-derived). Tape is wired on IB / Binance / Ironbeam (+ crypto venues, Simulated); NT / cTrader / Alpaca / LSE throw `NotSupportedException` on `SubscribeTradesAsync`.
- **Subscribe to BOTH** `IMarketDataIngest.Subscribe(quotes)` and `IMarketDataIngest.SubscribeTrades` — Lee-Ready needs bid/ask context for aggressor classification.
- **Aggressor inference** uses `Core/MarketData/Microstructure.ClassifyAggressor` (Lee-Ready). Do not reimplement.
- **Log panel** — `LogEntries : ObservableCollection<LogEntry>` bound to a right-hand panel; auto-scroll-to-end on append; cap ~200 lines. Levels: `INFO / DATA / REGIME / SPIKE / SIGNAL / ENTRY / EXIT / CONFIRM / ERROR`. Don't invent new levels per cube.

## Signal-independence sanity check

For a 3-axis cube, the three signals MUST be statistically independent over the relevant window. Beware: in a single window, **Cumulative Volume Delta ≈ 2·aggressor_ratio − 1**, so axes are NOT orthogonal. The Order Flow Cube *engine* has this caveat documented; the *live UI calculator* fixes it by using three different windows (recent 50 / trend 500 / baseline 2000) so signals are statistically separable for viz.

Before backtesting any new cube: compute pairwise correlation of the three signals on a real instrument over a representative window. If `|ρ| > 0.5` on any pair, redefine the axes.

## Hard rules

- No new 3D library — Helix Toolkit only.
- No `Place Trade` button on a cube window. Signal-mode by rule.
- No silent quote-synthesis fallback when trade tape is unsupported — fail loudly so the user picks an IB instrument or shipping the right broker first.
- Cube engine and Cube live UI share the same name prefix (`orderflow.cube` ↔ `OrderFlowCube`) — don't rename one without the other.

## Reference reads

- `src/TradingTerminal.Strategies.OrderFlowCube/` — shipped scatter-cube template.
- `src/TradingTerminal.Strategies.OrderFlowSurfaceSpike/` — shipped heightmap-surface template.
- `src/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowCubeStrategy.cs` — engine template.
- `D:\Github\DaxAlgo Terminal\ideas.md` — full ideas backlog.
- `Core/MarketData/Microstructure.cs` — `ClassifyAggressor`, microprice, queue imbalance.

See also: [[project-strategy-ideas]] (memory) for the per-cube notes and capability matrix.
