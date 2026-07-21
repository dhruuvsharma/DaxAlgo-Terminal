# ADR-0010 — Isolated backtest worker and single strategy kernel

**Date** 2026-07-21 · **Status** accepted

**Context.** Backtests are CPU- and memory-heavy, can run externally supplied strategy code, and must not make
the interactive terminal unresponsive. The existing Studio runs the managed engine inside the WPF
process, while the optional C++ runner implements different strategy behavior. Requiring a separate
live and backtest strategy implementation would create permanent drift.

**Decision.** The terminal owns a one-shot, WPF-free backtest worker process for each job. The parent
writes a versioned JSON request into a private job directory, consumes bounded NDJSON progress, and
reads only an atomically published result manifest and artifacts. Cancellation, timeout, malformed
output, crash, and terminal shutdown terminate the complete child process tree. The worker receives
immutable input references and has no broker, credential, network, canonical-store mutation, or live
order services.

The process and Windows Job Object boundary is operational isolation, not a same-user security sandbox.
Strategy code retains the worker account's filesystem, network, and framework authority, so marketplace
execution remains limited to trusted publishers until a restricted execution boundary is approved.

`TradingTerminal.Backtest.Engine` is the canonical behavioral oracle. A strategy is authored once as
a headless engine kernel; live signal hosts and the worker load that same versioned strategy assembly.
Optional WPF presentation is a separate companion assembly and contains no strategy math. The current
legacy contracts may be adapted during migration, but new consumers must not introduce another engine
or a second strategy implementation.

The first worker is managed .NET. Native C++ is an optional backend behind the same protocol only after
profiling identifies a worthwhile bottleneck and golden-tape parity proves equivalent ordered events,
orders, fills, costs, trades, metrics, and artifacts. A shared strategy id alone is not evidence of
equivalence.

**Consequences.** A worker failure cannot directly crash the terminal, UI cancellation is deterministic,
and durable artifacts are recoverable after failure. Protocol and artifact schemas require explicit
versions and compatibility checks. The first delivery moves Studio single runs for supported built-in
kernels and immutable inputs. ADR-0012 adds external exact-hash `.daxstrategy` loading; optimization,
walk-forward analysis, and remaining clients migrate incrementally. Unsupported modeling or execution
options fail explicitly
instead of being silently ignored. ADR-0007 still applies: simulated orders exist only inside backtests;
the live terminal remains data/signals only.
