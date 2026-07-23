# ADR-0012 — Immutable strategy install and worker activation

**Date** 2026-07-21 · **Status** accepted

**Context.** ADR-0011 defines a deterministic signed `.daxstrategy`, but inspection alone is not
permission to execute it. Re-signing preserves the manifest content root while changing the outer
archive and publisher evidence. ADR-0010 also requires external strategies to run in the one-shot
worker from exact bytes without loading their optional Windows UI into the terminal or worker.

**Decision.** Installation uses two immutable namespaces. Canonical manifest and payload bytes are
addressed by the manifest content root under `objects/sha256/`; the exact outer archive and its
structured receipt are addressed independently by archive SHA-256 under `evidence/sha256/`. Therefore
two signatures over identical content reuse one content object but retain separate evidence, and a new
signature can never mutate or inherit the trust of an existing content object. Small activation files
atomically point a strategy id at one content/evidence pair; they are locators, not trust anchors.

Install, activation, and resolve re-evaluate an explicit compatibility and publisher policy. Local
development may accept an unsigned bundle, while publisher mode requires a signature bound to a trusted
publisher/key pair. A present invalid or unknown signature is never treated as unsigned. Exact file sets,
canonical receipts, lengths, hashes, path containment, and reparse-point exclusions are checked again
when an installation is resolved. Staging is same-volume, create-new, flushed, and published by atomic
move. The legacy `.daxplugin` merge-copy installer remains a separate path and receives none of this
trust state. Verified-publisher evidence retains both the DSSE key id and the SHA-256 fingerprint of the
trusted SubjectPublicKeyInfo, so a reused label cannot disguise a changed key.

The backtest protocol has a closed `installed_bundle` source. It carries publisher/id/version, content
root, outer archive hash, required strategy-assembly hash, a bounded typed parameter bag, and closed trust
evidence: either unsigned local development or verified publisher with the key id, SPKI fingerprint, and
signature algorithm. It carries no store path or trust boolean. A separate top-level request value pins
the exact deployed `TradingTerminal.Backtest.Engine` assembly hash, preventing the host engine and bundle
engine identities from being conflated. Before request publication, the host client resolves the selected
installation under current policy and stages only the canonical manifest plus the graph-reachable engine
and private managed dependencies into the job's fixed `strategy/` directory. Windows UI, unrelated
resources, receipts, and store paths do not cross the worker boundary.

Packaging passively matches every private assembly reference to its bundled definition's full identity,
including version, culture, public-key token, Windows Runtime content type, and retargetable flag. The
worker then re-hashes and metadata-validates that exact staged closure before executing code. It loads the
manifest-named public `IStrategyEngineFactory` in a collectible load context with an exact private
assembly map. An external reference is accepted only when that exact assembly is actually present in the
worker runtime's trusted-platform set or is an explicitly shared SDK/Core contract; availability in the
broader desktop terminal is insufficient. The load context rejects unmanaged resolution and never scans
or probes nearby directories. Factory parameters must exactly match the declared typed schema without
silent clamping or fallback. The resulting `IBacktestStrategy` is adapted to the canonical engine,
disposed, and unloaded.

Before activation, the worker independently verifies the pinned host-engine and strategy-assembly
hashes. Result evidence reports their actual hashes, echoes the selected bundle trust evidence, and names
the verified staged closure; the client compares these values to the request. `StrategyAssemblyClosure`
names the engine files that were verified and made available to the load context, not assemblies proven
to have been loaded. The archive hash is store/client evidence for the exact selected archive; the worker
receives neither that archive nor its receipt.

**Consequences.** Any strategy implementing the one SDK factory can use the same implementation in an
isolated backtest without a second test-only strategy suite. Re-signing and key rotation preserve content
deduplication without confusing evidence. A verified publisher signature authenticates endorsement of the
signed content, and the archive hash binds store/client evidence to one outer archive; neither establishes
code safety.

A collectible load context plus the one-shot process tree under a Windows Job Object is a lifecycle,
identity, cleanup, and fault-isolation boundary, not an OS security sandbox: strategy code still runs
out-of-process under the same user token. Marketplace execution therefore remains trusted-publisher-only
until an approved restricted-token/AppContainer or VM boundary exists, or strategy code runs in a
separately constrained child with signal-only IPC. Marketplace attestations, revocation/freshness
distribution, installed strategy discovery/schema UX, update/rollback/GC UI, and external platforms remain
later work.
