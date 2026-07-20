# DaxAlgo Terminal — Codex guide

This repository has independent Windows/WPF and Linux/Avalonia application trees. Shared namespaces
do not imply shared implementation.

## Context first

For Windows work, read `.claude/context/index.md`, `symbols.md`, `deps.json`, and
`PROTOCOL.md`. For Linux work, use the corresponding masters under `.claude/context/linux/`.
Search the smallest generated `index/` or `symbols/` shard before source; project files and source
remain authoritative when generated context conflicts.

```powershell
powershell -File .claude/context/manage-context.ps1 summary
powershell -File .claude/context/manage-context.ps1 check
```

Use `deep-check` only when context machinery/artifacts change.

| Tree | Root | Solution | UI |
|---|---|---|---|
| Windows | `src/windows/` | `TradingTerminal.Windows.slnx` | WPF/MahApps |
| Linux | `src/linux/` | `TradingTerminal.Linux.slnx` | Avalonia |

Windows shells `App.Basic` and `App.Intermediate` are independent copies; apply shared behavior to
both while preserving edition composition. Linux owns separate backend/UI copies—never use a Windows
signature as evidence for Linux.

Windows strategies are external SDK plugins. Do not add `TradingTerminal.Strategies.*` projects
under `src/windows/` or wire strategy implementations into shells; use the SDK, template, sample,
loader, and authoring tools.

## Invariants

- Core has no WPF/Avalonia, broker SDK, storage implementation, or host dependency.
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
dotnet build TradingTerminal.Linux.slnx

dotnet test tests/TradingTerminal.Tests.Headless --filter FullyQualifiedName~<Area>
dotnet test tests/TradingTerminal.Tests --filter FullyQualifiedName~<Area>
dotnet test tests/linux/TradingTerminal.Tests.Headless --filter FullyQualifiedName~<Area>
```

Use one edition filter for ordinary Windows work, the full Windows solution for foundational or
cross-edition signatures, WPF tests for WPF changes, and Linux tests only for Linux scope.

Report files changed, checks actually run and results, risks, and deliberately deferred tree/edition
work.
