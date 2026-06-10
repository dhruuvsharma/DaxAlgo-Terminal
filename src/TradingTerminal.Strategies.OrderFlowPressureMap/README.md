# 1-Minute Order Flow Pressure Map

`TradingTerminal.Strategies.OrderFlowPressureMap` — live signal window for strategy id **`orderflow.pressuremap`**.

> A single heatmap matrix (ticker × time) over the S&P 100/500 universe that flags where unusual
> 1-minute volume is hitting and whether price is **absorbing** it or **breaking through**
> (Breakthrough / Breakdown), with per-cell intensity scaled by relative volume. It's a
> multi-ticker monitor rather than a per-instrument signal generator, but it lives in the
> Strategies pane because that's where the user drives it. **Display only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | ✅ (optional) | — |

L1 quotes + 1m bars drive the relative-volume and absorption/breakthrough classification;
L2 depth, when the broker provides it, sharpens the book-imbalance column.

## Universe & options

The ticker universe comes from `Sp100Sp500Catalog` (Core). Cell thresholds, lookback, and refresh
cadence bind from `OrderFlowPressureMapOptions` (`Core/Configuration`).

## Wiring

`AddOrderFlowPressureMapStrategy()` (called from `AddStrategyPlugins()` in the App shell)
registers the `ITradingStrategy` metadata, the VM/window pair, and the
`StrategyFactoryRegistration`, so it appears in the shell's Strategies pane and opens via
`IStrategyFactory` like every other strategy.
