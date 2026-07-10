# TradingTerminal.Correlation — correlation / PCA tool window

**Path** `src/windows/Tools/TradingTerminal.Correlation/` · 1,761 LOC / 10 files · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** Multi-instrument correlation matrix + PCA/EWMA views over hub/store data.
Math lives in `Core/Quant/` (`symbols/Core-Quant.md`) — this project is presentation + orchestration.

**Depends on** Core, Infrastructure, UI, UI.Core. **Surface** `symbols/Correlation.md`.
**Tests** Tests.Headless `~Correlation`.

**Common changes.** New statistic (add math to Core/Quant with numeric tests, surface here);
instrument-set UX (uses the shared `InstrumentPicker`). Load `quant-math` for the math,
`memory-safety` for streaming refresh paths.
