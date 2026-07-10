# TradingTerminal.Recording — tick recorder window

**Path** `src/windows/Tools/TradingTerminal.Recording/` · 423 LOC / 5 files · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** Records live streams to disk for later replay/analysis. Still writes parquet —
standing follow-up: drop parquet after AI/ML/Research consumers migrate off `ParquetTickReader`
(memory: project_recorder_parquet_followup).

**Depends on** Core, Infrastructure, UI, UI.Core. **Surface** `symbols/Recording.md`.
**Tests** Tests.Headless `~Recording`.

**Common changes.** Output format work (parquet exit path), stream selection UX. Recording ≠ the
canonical store — don't conflate; the store persists regardless (tick-primary).
