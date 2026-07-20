# PROTOCOL — mandatory context-first change contract

The objective is fast, evidence-based work: route from generated facts, inspect only the affected
source, make the smallest authorized change, and leave durable memory.

## Per request

1. **Load the correct masters before source.**
   - Windows: `.claude/context/index.md`, `symbols.md`, and `deps.json`.
   - Linux: `.claude/context/linux/index.md`, `symbols.md`, and `deps.json`.
   - When this repository is mounted under an overlay, also load that overlay's masters.
2. **Protect existing work.** Inspect `git status --short`; unrelated dirty files belong to the
   user. Never discard, overwrite, stage, or commit them.
3. **Create durable task memory for material changes.** Use
   `tasks/<YYYY-MM-DD-HHMM>-<slug>.md` following `tasks/README.md`. Record the goal, likely files,
   decisions, blast radius, checks, and final diff. Read-only answers and trivial known-path edits do
   not need a scratchpad.
4. **Navigate lazily.** Search the matching generated `index/` and `symbols/` shard first, then
   use `rg` and ranged reads around the exact source hit. Generated context routes discovery; source
   and project files remain authoritative.
5. **Name the blast radius.** Use `deps.json` to identify direct dependents, editions, tests, and
   tree boundaries. Re-plan if evidence expands the authorized scope.
6. **Resolve only material ambiguity.** Ask when a missing choice would change behavior, compatibility,
   security, or scope. Otherwise state the assumption in the scratchpad and continue. If the request
   already authorizes implementation, do not pause for a second approval.
7. **Edit narrowly.** Use the smallest coherent diff, preserve architecture boundaries, and avoid
   unrelated cleanup. Parallelize only independent work with clear file ownership.
8. **Verify proportionally.** Start with syntax/static checks and the narrowest project, solution
   filter, or test filter that covers the change. Never use a bare `dotnet build` in this repository.
9. **Synchronize context.** Do not hand-edit generated `index/` or `symbols/` shards. Run the
   appropriate generator when paths or public symbols change, update `deps.json` when project
   references change, append `changelog.md`, and run freshness checks.
10. **Close the scratchpad and report.** Record files changed, checks actually run, results, risks,
    and deferred cross-tree work. Do not claim a check that was not executed.

## Scope multipliers

- A shared public shell change normally applies to both `App.Basic` and `App.Intermediate`; use
  `RECIPES/shell-fix-editions.md`. An overlay may impose an additional shell copy, but public code
  never imports or reproduces private implementation.
- Windows/WPF and Linux/Avalonia are independent trees. Touch both only when explicitly authorized
  and verify each from its own context layer.
- Windows strategies are external SDK plugins. Use `RECIPES/add-strategy.md`; never recreate
  first-party `src/windows/Strategies/TradingTerminal.Strategies.*` projects.
- External actions—issues, commits, pushes, PRs, releases, or messages—require explicit user approval.

## Hard stops

Stop and report when the change would cross the open-core boundary, expands to an unauthorized tree
or repository, needs credentials or destructive recovery, or remains build-broken after two focused
repair attempts. Never use destructive Git commands or `--no-verify`.

## Standing invariants

Core has no application-specific dependencies; MarketData stays below Infrastructure; broker SDK
types stay inside Infrastructure; canonical identity is `InstrumentId`; ingest is tick-primary and
non-blocking; provenance is preserved; view-models consume market-data seams rather than broker
streams; streaming UI is bounded and disposable; MVVM remains strict; sidecars bind to
`127.0.0.1`; the product introduces no live order-execution path.
