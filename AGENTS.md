# DaxAlgo Terminal — Codex guide

This repository owns the Windows/WPF open-core implementation. The Linux/Avalonia edition is an
independent private repository and is outside this workspace.

## Context first

Read `.claude/context/index.md`, `symbols.md`, `deps.json`, and `PROTOCOL.md` before source.
Search the smallest generated `index/` or `symbols/` shard before source; project files and source
remain authoritative when generated context conflicts.

```powershell
powershell -File .claude/context/manage-context.ps1 summary
powershell -File .claude/context/manage-context.ps1 check
```

Use `sync` after routed source/context changes and `deep-check` for final non-mutating verification;
both actions serialize simultaneous terminals. Do not invoke the generator directly.

| Tree | Root | Solution | UI |
|---|---|---|---|
| Windows | `src/windows/` | `TradingTerminal.Windows.slnx` | WPF/MahApps |

Windows shells `App.Basic` and `App.Intermediate` are independent copies; apply shared behavior to
both while preserving edition composition. Do not inspect or mirror the external Linux repository
unless the user explicitly opens it and expands scope.

Windows strategies are external SDK plugins. Do not add `TradingTerminal.Strategies.*` projects
under `src/windows/` or wire strategy implementations into shells; use the SDK, template, sample,
loader, and authoring tools.

## Invariants

- Core has no WPF, broker SDK, storage implementation, or host dependency.
- MarketData depends on Core and stays below Infrastructure; broker SDK types stay in Infrastructure.
- `InstrumentId` is canonical; preserve event/ingest time, source, sequence, and approximation.
- Ingest is tick-primary and non-blocking; VMs consume hub/ingest/store seams, never broker streams.
- MVVM is strict. Streaming UI uses bounded buffers, coalesced redraw, and deterministic teardown.
- Use the shared Activity Log. Pair credentialed login forms with credentialed brokers.
- Sidecars bind to `127.0.0.1`; introduce no live order-execution path.

## Skills and agents

Repository skills are under `.agents/skills/`; start with `navigator`, then load only the matching
domain skill. Custom agents under `.codex/agents/` are optional—use one only when its current target
and independent work justify delegation.

Follow `.claude/context/PROTOCOL.md`: inspect status, keep material task memory, derive blast radius,
edit narrowly, verify proportionally, refresh generated context, and close the task record. Never
discard unrelated work or perform external GitHub/Git actions without explicit authorization.

## Build and test

Never use a bare `dotnet build` here:

```powershell
dotnet build TradingTerminal.Windows.Basic.slnf
dotnet build TradingTerminal.Windows.Intermediate.slnf
dotnet build TradingTerminal.Windows.slnx

dotnet test tests/TradingTerminal.Tests.Headless --filter FullyQualifiedName~<Area>
dotnet test tests/TradingTerminal.Tests --filter FullyQualifiedName~<Area>
```

Use one edition filter for ordinary Windows work, the full Windows solution for foundational or
cross-edition signatures, and WPF tests for WPF changes.

Report files changed, checks actually run and results, risks, and deliberately deferred tree/edition
work.
