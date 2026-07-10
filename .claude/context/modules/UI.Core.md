# TradingTerminal.UI.Core — MVVM base layer for live windows

**Path** `src/windows/UI/TradingTerminal.UI.Core/` · 2,246 LOC / 21 files · **Editions** B I P · **Blast: HIGH (13 dependents incl. every strategy via Sdk.Wpf)**

**Purpose.** The MVVM backbone every live window builds on: `ViewModelBase`,
**`LiveSignalStrategyViewModelBase`** (940 LOC — hub subscription pumps, pause/presets/CSV export,
chrome-bar contract, IDisposable teardown), **`LiveStrategyHostServices`** (record bundle:
Repository + Hub + Ingest + Store + BrokerSelector + ActivityLog — the ONLY ctor dependency of
strategy VMs), **`InMemoryLogSink`** (`Logging/InMemoryLogSink.cs:24` — the universal Activity Log),
`StrategyWindowBase`, `StrategyChromeBar` (binds by convention).

**Depends on** Core only. **Depended by** UI, Sdk.Wpf (→ all 9 strategies), all chart/tool
projects, Settings, Tests.Headless.

**Surface** `symbols/UI.Core.md` — read it before asking "what does the base give me"
(that question used to cost a 940-LOC file read).

**Invariants.** Part of the strategy-plugin SDK surface (ADR-0008) — a breaking change here breaks
every installed plugin; version-gate. Bounded channels + batch-drain + coalesced redraw in the
streaming bases (`memory-safety` skill; `leakcheck-on-stop` enforces). One Activity Log, ever.

**Tests** Tests.Headless `~UiCore` / strategy-base tests. **Common changes.** New base-VM feature
(design once here instead of 9 copies); chrome-bar additions; log-sink filtering.
