# TradingTerminal.AdvancedMarketRegime

The **Advanced live market regime** tool: a WPF port of a TradingView "Multi-Timeframe Indicator
Dashboard" — 18 indicator rows × up to 8 timeframe columns, every row and column independently
toggleable.

**Rows:** RSI, MACD, CCI, MA 9/21/50, 3MA stack, VWAP, SuperTrend, ATR, ATR Reg, STD, POC Pos,
TRD (range position), Delta, Cum Δ, Vol B/S, and a composite **Trend** needle gauge (−8..+8 score
rendered as −90°..+90°).

**Columns:** 1m, 3m, 5m, 15m, 20m, 30m, 1H, 1D by default. 20m/30m are not broker-native bar
sizes — the provider fetches one 1-minute base series and aggregates up per column
(`BarTimeframeAggregator` in Core); 1D is fetched directly.

## How it fits

- Pure math + models: `TradingTerminal.Core/MarketData/AdvancedRegime/` (calculator, aggregator,
  bar indicators, settings, `IAdvancedRegimeProvider`).
- Data: `AdvancedRegimeService` (Infrastructure) pulls historical bars through
  `IMarketDataRepository` — the VM never touches a broker client.
- Delta / Cum Δ / Vol B/S are **bar proxies** (candle direction × volume), so no trade tape is
  required and the dashboard works on every broker.
- Snapshots are computed with all rows enabled; row toggles and the Show direction/value flags
  re-project from the cached snapshot without refetching. Column toggles apply on the next
  analyze. Optional auto-refresh re-analyzes on a fixed interval.

## Wiring

`AddAdvancedMarketRegimeSurface()` registers `IAdvancedRegimeProvider`, the view-model, and the
view; the App shell opens it from **Tools → Advanced market regime…** (tab id
`tools.regime.advanced`).
