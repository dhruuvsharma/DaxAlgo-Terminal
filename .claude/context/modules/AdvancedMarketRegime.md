# TradingTerminal.AdvancedMarketRegime — regime composite window

**Path** `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** The advanced market-regime tool (the old MarketRegime/InstrumentRegime windows were
removed): composite of FRED + Yahoo + Fear&Greed + AAII + per-instrument analyzers; grid of
`AdvancedRegimeCell/Column/Snapshot` (types in `Core/` — `symbols/Core-Root.md` / regime files;
calculators in `Infrastructure/Regime/` → `symbols/Infrastructure-Regime.md`).

**Depends on** Core, Infrastructure, UI, UI.Core. **Surface** `symbols/AdvancedMarketRegime.md`.
**Tests** Tests.Headless `~Regime`.

**Common changes.** New indicator column (calculator in Infrastructure/Regime + Core row type +
grid binding); data-source resilience (external feeds fail soft). `quant-math` for indicator math.
