# Strategies.CumulativeDelta — CVD strategy

Shape: `modules/Strategies-family.md`. **2,700 LOC / 8 files.**
- Tape-primary cumulative-delta signal; needs TradeTape (IB/Binance/Ironbeam).
- `CumulativeDeltaViewModel.cs` is **1,355 LOC** (largest strategy VM — grep + ranged reads);
  `CumulativeDeltaWindow.xaml` 734.
- Surface `symbols/Strategies.CumulativeDelta.md` (`CumulativeDeltaPlugin` @ :13).
- Kernel referenced directly by Tests.Headless.
