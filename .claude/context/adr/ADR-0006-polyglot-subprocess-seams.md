# ADR-0006 — Polyglot components are subprocess + HTTP/JSON seams

**Status** accepted

**Context.** AI analysis (Python/LangGraph/TA-Lib), research reproduction, and the C++
backtester must not drag Python/native dependencies into the C# build.

**Decision.** Sidecars are separate processes reached over HTTP/JSON behind hot-swappable
seams (`IAiAnalystClient` Null/Http, `IPaperIngestClient` Null/Http), bound to **127.0.0.1
only**. The app can manage the sidecar lifecycle (IHostedService + Job Object kill-on-exit).
QuantConnect/LEAN is a subprocess backtest seam, not a broker.

**Consequences.** C# build stays dependency-clean; sidecars hot-swap Null↔Http via options.
Never bind 0.0.0.0. Untrusted third-party code (Paper Lab) additionally goes through the
deny-by-default sandbox (`untrusted-execution` skill) — never in-process.
