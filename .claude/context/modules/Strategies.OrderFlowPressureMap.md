# Strategies.OrderFlowPressureMap — multi-ticker pressure monitor

Shape: `modules/Strategies-family.md`. **1,327 LOC / 10 files** (VM 661).
- MULTI-ticker monitor — the canonical example of the strategy-vs-tool rule: it registers
  `ITradingStrategy`, so it is a strategy project even though it "monitors" (drift corrected
  2026-06-10; don't regress it to a tool).
- Multi-asset subscriptions ⇒ N hub pumps; bound each channel, dispose all on close.
- Surface `symbols/Strategies.OrderFlowPressureMap.md` (`OrderFlowPressureMapPlugin` @ :8).
- Referenced by the WPF test project (`TradingTerminal.Tests`), not Headless.
