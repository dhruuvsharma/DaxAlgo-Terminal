# TradingTerminal.VolumeFootprint — footprint chart + next-bar ML ghosts

**Path** `src/windows/Charts/TradingTerminal.VolumeFootprint/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** Volume-footprint chart with the online-RLS next-bar forecaster (POC/vol/delta ghost
projections vs regression baseline, warm-start from store).

**Key files.** `VolumeFootprintViewModel.cs` (1,093), `VolumeFootprintWindow.xaml.cs` (814 —
render-heavy code-behind is the historical leak site), `.xaml` (445). ML math:
`Core/Ml/FootprintNextBarPredictor.cs` (408) → `symbols/Core-Ml.md`. **Surface** `symbols/VolumeFootprint.md`.

**Depends on** Core, Infrastructure, UI, UI.Core.

**Invariants.** THE original 20 GB tape-leak window — bounded channel + batch-drain + coalesced
render timer are load-bearing; never add per-trade UI marshals or per-redraw allocations
(`leakcheck-on-stop` blocks). Tape via hub `Trades(id)` only.

**Tests** Tests.Headless `~Footprint`. **Common changes.** Predictor features, ghost rendering,
imbalance highlighting. Load `memory-safety` FIRST, `quant-math` for estimator changes.
