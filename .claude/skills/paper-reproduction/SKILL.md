---
name: paper-reproduction
description: The recipe for the Paper Lab pipeline — turn a research paper into a queued, sandboxed reproduction whose outputs bridge into the backtest engine as a paper-tagged strategy (autoarxiv-style, scoped to quant/trading papers). Use when touching src/TradingTerminal.Core/Research/, src/TradingTerminal.Infrastructure/Research/, the TradingTerminal.Ai.PaperLab window, IReproOrchestrator/IReproJobStore/IReproSignalBridge/ReproducedSignalStrategyKernel, or whenever the user asks to read/implement/experiment/test/reproduce/replicate a paper as a tradeable strategy. Pair with untrusted-execution (sandbox) and paper-ingestion (arXiv seam).
---

# Paper Reproduction (Paper Lab)

Turn a paper URL into a **queued, sandboxed reproduction job** whose outputs (signals / weights /
predictions) bridge into the canonical backtest engine as a **paper-tagged strategy**. The desktop
app is a thin client over a scalable reproduction backend. Precedent for the *output* shape: the
clean-room FilteredOrderFlow strategy (arXiv:2507.22712) — the bridged result is signal data + our
kernel, **not** vendored paper code.

## The pipeline (4 stages, hardest last)

1. **Ingest** — paper URL → `PaperRef` + candidate `RepoRef[]`. Seam `IPaperIngestClient` (Null/Http,
   subprocess + HTTP/JSON, `127.0.0.1` only). See the `paper-ingestion` skill.
2. **Resolve + run** — clone the repo at a pinned commit, resolve its environment, run a **minimal
   reproduction**, estimate **full replication cost**. Runs ONLY inside a sandbox — see the
   `untrusted-execution` skill. Seam `ISandboxRunner`.
3. **Bridge** — map the reproduced outputs onto `InstrumentId`-keyed `ReproducedSignal`s, replayed
   through a `ReproducedSignalStrategyKernel : IStrategyKernel`. Seam `IReproSignalBridge`.
4. **Score + save** — `IReplicationConfidenceScorer` → `ReplicationConfidence`; register a paper-repro
   `BacktestStrategyOption` (with `ResearchPaperUrl`) so it shows in the Studio catalog with the
   clickable paper pill.

## Layer placement (do not break the graph)

- **All domain types + seams in `Core/Research/`** — records/enums + interfaces only, SDK-free.
  `PaperRef`, `RepoRef`, `EnvHash`, `ReproSpec`, `ReproJob`(+`ReproStatus`), `ReproResult`,
  `ReplicationConfidence`, `ReplicationCostEstimate`, `SandboxKind`, `SandboxQuota`, `SandboxPolicy`,
  `ReproducedSignal`. Options in `Core/Configuration/` (`ResearchReproOptions`, `SandboxOptions`).
- **Concretes in `Infrastructure/Research/`** — SQLite store, HTTP/process clients, the sandbox
  runner, the bridge, the confidence scorer, the `ReproducedSignalStrategyKernel`. Third-party paper
  code NEVER touches the C# build — it runs inside the container/VM.
- **Window `TradingTerminal.Ai.PaperLab/`** — mirrors `TradingTerminal.Ai.MarketAnalyst` (transient
  VM+View, `AddPaperLab()`, opened via `OpenHostedTool<…>` from `MainWindowViewModel`). References
  Core/UI/Infrastructure only.
- **`App` wires concretes** — `AddPaperResearch(config)` + `AddPaperLab()` + one menu command. No
  shell `switch` edits (OCP).

## Patterns to clone (don't re-derive)

| Need | Clone from |
|---|---|
| Null/Http seam, never-throw contract | `IAiAnalystClient` / `HttpAiAnalystClient` |
| SQLite manifest + sha256 + retention + cache lookup | `ArchiveManifestStore` (namespace `TradingTerminal.Infrastructure.MarketData.Archive`, lives in Infrastructure) |
| Process spawn / timeout / kill-process-tree | `LeanProcessRunner` (`src/TradingTerminal.QuantConnect/`) |
| Strategy reaches the engine | `IStrategyKernel` + `StrategyKernelRegistry` + `BacktestStrategyOption.BacktestBuild`/`CreateForBacktest` |
| Runtime "save as strategy" into the catalog | `StrategyAuthoringViewModel` registration path |
| Status without polling | `IObservable<ReproJob>` (Rx), like `IObservable<ConnectionState>` |

## Scalability contract (the point of this feature)

- **Job model**: async, queued, cancellable, status-streamed; `LocalReproOrchestrator` requeues
  unfinished jobs from `IReproJobStore` on startup (survives app restart).
- **Cache**: keyed by `(arXiv id, repo commit, config hash)` — `FindCovering`-style lookup before
  submit; identical spec returns the cached `ReproResult`, no new container.
- **Pluggable backend**: `ISandboxRunner` (Docker now; WSL2 / `HttpReproOrchestrator` remote-pool
  later) — the orchestrator/runner seams already abstract local-vs-remote so the backend can move
  off-machine with **no UI change**.
- **Budget**: `ReplicationCostEstimate` gates the "run full replication" button; quotas enforced.
- **Provenance everywhere**: paper id + repo commit + env hash + confidence ride on `ReproResult`
  and every `ReproducedSignal` — never strip (same discipline as canonical market-data records).

## What NOT to do

- Don't run paper code in-process or let it reach the canonical store / credentials — only via
  `ISandboxRunner` (see `untrusted-execution`).
- Don't add Python/native deps to the C# build — env-resolution + LLM logic live in the sidecar
  (`tools/python-ml/`); C# only orchestrates over HTTP/JSON.
- Don't add a live order path — the reproduced strategy reaches the engine only through
  `IStrategyKernel`; data/signals only.
- Don't silently trust a low-fidelity run — surface `ReplicationConfidence`; fail loudly when the
  reproduced signal needs data the engine feed can't supply (depth / full tape), mirroring the
  trade-tape capability check.
- Don't vendor the paper's repo code into the tree — bridge the *signal*, credit the source, keep
  the clean-room rule.
