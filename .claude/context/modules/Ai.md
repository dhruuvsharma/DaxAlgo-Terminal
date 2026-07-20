# TradingTerminal.Ai — AI analyst seam (shared seam ONLY)

**Path** `src/windows/AI/TradingTerminal.Ai/` · **Editions** B I P · **Blast: med**

**Purpose.** The thin seam to the Python sidecar: `AddAiAnalyst` DI, the
`AiAnalystEnricher` (notification pipeline), Null↔Http client swap via `IOptionsMonitor`.
The `IAiAnalystClient` interface itself lives in `Core/AiAnalyst/`. This shared Windows tree
contains the seam, not the removed first-party AI tool-window projects.

**Depends on** Core, Infrastructure (actual csproj refs — CLAUDE.md's "Ai → UI, MarketData" is stale).
**Depended by** both shells.

**Surface** `symbols/Ai.md`. Sidecar code: `tools/python-ml/` (not in the C# build).

**Invariants.** Subprocess + HTTP/JSON only (ADR-0006); 127.0.0.1 binding; hot-swappable Null client
keeps the app fully functional with the sidecar down.

**Tests** Tests.Headless `~Ai`. **Common changes.** New sidecar endpoint (extend `IAiAnalystClient`
in Core + Http/Null impls + sidecar route in `tools/python-ml/`); enricher behavior. Load `ai-analyst` skill.
