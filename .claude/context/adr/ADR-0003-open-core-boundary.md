# ADR-0003 — Open-core split: Professional code in a private overlay repo

**Date** 2026-07-09 (#21) · **Status** accepted

**Context.** Monetize the Professional edition while keeping the core open.

**Decision.** This repo is the open-source core (AGPL-3.0; `src/windows/Sdk/` stays MIT). The
Professional edition lives in private **DaxAlgo-Terminal-Pro**, which consumes this repo as its
`public/` git submodule. Moved there: the Professional shell, Ai.* tool windows, Ml.* windows,
SurfaceLab, BubbleChart, LseBacktest, QuantConnect, Backtest.Cli, installer, Tests.Pro.

**Consequences.** NEVER copy code from the Pro repo into this world-readable repo (hard stop in
PROTOCOL.md). Shared/backend work lands here first; the Pro repo bumps its submodule pin.
Shell fixes are ×3 across two repos. The Linux tree still carries ports of some Pro windows —
pruning them is an open decision.
