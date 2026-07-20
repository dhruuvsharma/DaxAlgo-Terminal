# TradingTerminal.Charts — TradingView-style charts window (WebView2)

**Path** `src/windows/Charts/TradingTerminal.Charts/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** The Charts menu's candlestick window hosting a WebView2 chart (TradingView-style),
fed from the hub/store. Windows-only tech (no Linux mirror of WebView2).

**DI** `AddChartsSurface` — `ChartsServiceCollectionExtensions.cs`. **Surface** `symbols/Charts.md`.
**Depends on** Core, Infrastructure, UI, UI.Core. **Consumed by** both shells (Charts menu).

**Invariants.** VM subscribes via `IMarketDataHub` only; bounded channel + coalesced redraw
(`memory-safety`); dispose teardown on window close (`leakcheck-on-stop` watches).

**Tests** Tests.Headless `~Charts`. **Common changes.** Indicator overlays, symbol/timeframe UX,
WebView2 bridge messages. `tool-windows` agent owns it if spawning; usually inline.
