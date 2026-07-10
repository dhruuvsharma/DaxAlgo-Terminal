# Strategies.ImbalanceHeatFront — imbalance heat-front tracker

Shape: `modules/Strategies-family.md`. **1,391 LOC / 8 files.**
- Tracks propagating order-flow imbalance "fronts" across price levels; tape + L1.
- Surface `symbols/Strategies.ImbalanceHeatFront.md` (`ImbalanceHeatFrontPlugin` @ :8).
- Kernel referenced directly by Tests.Headless. In the C++-port first-slice trio
  (with SigmaIcFlow + CumulativeDelta) — keep its math self-contained.
