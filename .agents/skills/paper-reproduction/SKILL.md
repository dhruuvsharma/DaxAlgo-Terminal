---
name: paper-reproduction
description: "Implement or maintain research-paper reproduction in DaxAlgo Terminal: paper ingestion, queued sandbox jobs, provenance-preserving signal bridges, confidence scoring, and backtest integration. Use for Windows backend work under src/windows/Core/TradingTerminal.Core/Research/ or src/windows/Pipeline/TradingTerminal.Infrastructure/Research/, or for the separate Linux PaperLab UI. Always load paper-ingestion and untrusted-execution; never assume the Windows and Linux trees share UI or implementations."
---

# Paper Reproduction

Turn a research paper into a queued, sandboxed reproduction whose validated outputs can feed the
backtest engine. Bridge signals and provenance; never vendor or execute paper code inside the host.

## Choose the tree first

- Windows backend: `src/windows/Core/TradingTerminal.Core/Research/` and
  `src/windows/Pipeline/TradingTerminal.Infrastructure/Research/`.
- Linux backend: the equivalent `src/linux/Core/` and `src/linux/Pipeline/` paths; verify independently.
- PaperLab UI: `src/linux/AI/TradingTerminal.Ai.PaperLab/` only, composed by the Avalonia shell.

There is no Windows PaperLab project. A Windows backend change does not authorize Linux UI changes,
and a Linux UI change must use the Linux context layer and Avalonia contracts.

## Pipeline

1. Ingest a paper URL into `PaperRef` and pinned `RepoRef` candidates through
   `IPaperIngestClient`; keep sidecars on `127.0.0.1`.
2. Resolve the environment and run a minimal reproduction only through `ISandboxRunner`; enforce
   quotas, cancellation and deny-by-default egress.
3. Validate declared artifacts and map them to `InstrumentId`-keyed `ReproducedSignal`s through
   `IReproSignalBridge`.
4. Score confidence, retain paper/repository/environment provenance, and expose a paper-tagged
   backtest option through the existing strategy/backtest seams.

## Layer placement

- Put records, enums, options and interfaces in that tree's Core Research/Configuration folders.
- Put stores, clients, orchestrators, sandbox runners, bridges and scorers in that tree's
  Infrastructure Research folder.
- Keep Python/native environment logic in `tools/python-ml/`; C# orchestrates over process or
  loopback HTTP boundaries.
- Keep Linux PaperLab as a thin client over those seams. Do not move UI types into Core or Pipeline.

## Reliability and scale

- Queue jobs asynchronously; stream status; make cancellation idempotent; requeue unfinished work
  from `IReproJobStore` after restart.
- Cache by paper identity, pinned repository commit and configuration hash before launching a runner.
- Run a minimal reproduction before a budget-approved full replication.
- Preserve paper ID, commit, environment hash, artifacts and confidence on results and signals.
- Fold expected external failures into explicit failed/unavailable results rather than crashing the UI.

## Security invariants

- Execute untrusted code only in the configured container/VM/WSL boundary.
- Never mount credentials, the canonical store, user profiles or broad host paths.
- Apply CPU, memory, process, disk and wall-clock limits; kill the complete process tree.
- Accept output only through declared, validated artifacts.
- Never add a live order path; reproduced output is data/signals for backtesting.

When cloning process-lifetime behavior, verify the implementation in the selected tree first.
