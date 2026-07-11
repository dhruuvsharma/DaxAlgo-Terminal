# ADR-0009 — Out-of-process strategy host (untrusted-plugin isolation tier)

**Date** written 2026-07-12 (plugin-security initiative, #23) · **Status** proposed — spec only, not scheduled

**Context.** Strategy plugins load in-process (ADR-0008). An in-process .NET plugin runs with the
host's full privileges: it can P/Invoke, spawn processes, reflect past DI, and read the broker session,
the DPAPI-scoped credential store, and the SQLite stores. CAS/AppDomain sandboxing is dead in modern
.NET, so there is **no in-process way to enforce** Dhruv's requirement that a strategy not touch user
accounts or the system. #23 ships layered *deterrence + detection + containment-by-curation* — hash-
pinned first-party trust, curated signing, the add-only registrar guard, the static IL policy scan,
integrity + revocation, and unsigned-plugin consent — and is honest in code and docs that these are
tripwires, not a sandbox. The only true enforcement is to run untrusted strategy code in a **separate
process** with a restricted token. This ADR specifies that tier so the decision to build it is an
informed one; it is deliberately **not** scheduled for distribution v1.

**Decision (proposed).** A strategy the host cannot vouch for (unsigned/unpinned, or one the user
marks "isolate") runs in a child **strategy-host process**, one per plugin, launched with a restricted
token (ideally an AppContainer / low-integrity token: no network unless the plugin declares it, no
access to the user profile, the credential store, or the canonical data stores). The host and child
speak over a local IPC channel:

- **Market data host → child:** a shared-memory ring buffer (or named pipes) carrying the same
  `IMarketDataHub` events (`Quote` / `TradePrint` / `OhlcvBar` / `DepthSnapshot` by `InstrumentId`).
  Latency budget vs. the in-proc hub is the open engineering question — the tape-primary scalpers are
  the sensitive case.
- **Child → host:** signal/observation messages only (this build is data/signals — no order path per
  ADR-0007), which the host surfaces in the catalog exactly like an in-proc strategy's.
- **Strategy windows:** the child owns its own top-level HWNDs (its `MetroWindow`), reparented or
  positioned by the host; the host never loads the plugin's WPF types.
- **Lifecycle:** the host owns a kill-tree over the child (Job Object, kill-on-close), a watchdog for
  hangs/crashes (reuse the existing plugin fault watchdog), and the same quarantine-on-fault path.

**Consequences.** True isolation for untrusted strategies, at real cost:

- **What breaks.** `LiveSignalStrategyViewModelBase` and the in-proc custom WPF controls assume the
  strategy VM shares the host's process and dispatcher; a cross-process strategy can't reuse them
  as-is. In-proc chart/Helix controls a strategy window builds on would need a proxy or a redraw over
  IPC.
- **Cost.** A serialization + transport layer for the hub, per-instrument fan-out to N children, HWND
  reparenting, and the restricted-token plumbing. This is a multi-week subsystem, not a flag.
- **Relationship to #23.** The in-proc layers stay: even with an out-of-proc tier, first-party and
  curated-signed plugins keep running in-process (they're trusted, and pay no IPC tax). The out-of-proc
  host is the escape valve for *untrusted* code that the user wants to run anyway — a stronger version
  of today's consent + DEV badge. The `untrusted-execution` skill's deny-by-default sandbox posture
  (used by the Paper Lab repro engine) is the reference model.
- **Not decided here.** Whether to build it, and when — that waits on real third-party-plugin demand
  and a latency spike proving the shared-memory hub transport meets the scalpers' budget.

Supersedes nothing. Complements ADR-0007 (no live order execution — bounds the blast radius) and
ADR-0008 (in-process plugin model — this is the isolation tier layered on top).
