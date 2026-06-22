---
name: paper-ingestion
description: How the arXiv/paper-to-repo ingestion seam and the reproduction job/manifest cache work in DaxAlgo Terminal. Covers IPaperIngestClient (Null/Http, subprocess + HTTP/JSON, 127.0.0.1 only) resolving a paper URL to PaperRef + candidate RepoRef[], and the SQLite ReproJobStore cloned from ArchiveManifestStore (cache keyed by arXiv id + repo commit + config hash, sha256 artifact refs, retention). Use when touching IPaperIngestClient, ReproJobStore, HttpPaperIngestClient, the repro sidecar endpoints, or wiring AddPaperResearch().
---

# Paper Ingestion + Job Cache

The front of the Paper Lab pipeline: resolve a paper URL to something runnable, and persist the
reproduction jobs/results so the same paper is never re-run needlessly. Both pieces are deliberately
boring — they clone existing, proven patterns.

## Ingestion seam — `IPaperIngestClient`

`ResolveAsync(string url, ct) -> (PaperRef paper, IReadOnlyList<RepoRef> repos)`.

- Clone the shape of **`IAiAnalystClient`**: `IsAvailable`; methods never throw; failure surfaces as
  an empty/`Failed` result, not an exception. Hot-swap Null↔Http via `IOptionsMonitor`.
- `NullPaperIngestClient` (default) lets the app build/run with no sidecar (degrade-gracefully, same
  as the AI analyst). `HttpPaperIngestClient` mirrors `HttpAiAnalystClient`: named `HttpClient`,
  snake_case JSON, fold-all-failures, sidecar bound to **`127.0.0.1` only**.
- Resolution logic (arXiv metadata + PDF + discovering the code repo via Papers-with-Code / README /
  arXiv links) lives in the **Python sidecar** (`tools/python-ml/`) — no native deps in the C# build.
- `PaperRef` carries arXiv id + title + the paper URL (becomes `ResearchPaperUrl` on the saved
  strategy). `RepoRef` carries the git URL + a **pinned commit** (the cache key depends on it).

## Job / manifest store — `ReproJobStore`

Persisted in SQLite, **cloned from `ArchiveManifestStore`** (namespace
`TradingTerminal.Infrastructure.MarketData.Archive`, lives in `Infrastructure`):

- `internal sealed`, private write connection, `EnsureSchema`, micros timestamps, JSON-serialized
  blob/artifact refs with sha256, retention / soft-delete (`MarkLocalDeleted` equivalent).
- **Cache key = `(PaperRef.ArxivId, RepoRef.Commit, ConfigHash)`** → a `FindCovering`-style lookup
  *before* submit returns the existing `ReproJob`/`ReproResult` instead of spawning a new container.
- On startup, `LocalReproOrchestrator` reads back unfinished (`Queued`/`Running`) jobs and requeues
  them — the store is what makes jobs survive an app restart.

## Wiring

- `AddPaperResearch(config)` (in `ResearchReproServiceCollectionExtensions`) registers the store +
  ingest client (Null by default, Http when `ResearchReproOptions` points at a sidecar) + the
  orchestrator + the runner (by `SandboxOptions.Kind`).
- Called from `App.xaml.cs` alongside `AddPaperLab()`.

## Layer placement

- `IPaperIngestClient`, `PaperRef`, `RepoRef`, `ReproJob`, `ReproSpec`, `ReproResult` → `Core/Research/`.
- `HttpPaperIngestClient`, `NullPaperIngestClient`, `ReproJobStore` → `Infrastructure/Research/`.

## What NOT to do

- Don't throw across `IPaperIngestClient` — fold failures (no sidecar, bad URL, no repo found) into
  an empty/`Failed` result.
- Don't bind the sidecar to `0.0.0.0` — `127.0.0.1` only.
- Don't skip the commit pin — without it the cache key is meaningless and reproductions aren't
  deterministic.
- Don't put resolution/scraping logic in the C# build — it belongs in the sidecar.
