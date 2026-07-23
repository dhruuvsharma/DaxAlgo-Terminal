# MAINTENANCE — keeping the context layer honest

## Regeneration (mechanical parts)
`powershell -File .claude/context/manage-context.ps1 sync` from the repo root runs the generator
under a cross-process lock, replaces `index/*.md` and `symbols/*.md`, prunes orphans, and verifies
the result. Use `... deep-check` for a non-mutating byte-for-byte check. Legacy direct generator
calls delegate to these locked actions. Hand-written files
(`index.md`, `symbols.md`, `deps.json`, `modules/`, `adr/`, `RECIPES/`, `PROTOCOL.md`,
`glossary.md`) are never touched by the script — update those by hand when the graph changes.

## When
- **After any routed `.cs`/`.xaml` change**: run the manager's `sync` action before finishing. The
  `docsync-on-stop` hook uses a locked fingerprint gate and blocks if stable generated rows, LOC,
  purposes, signatures, or anchors lag the working tree.
- **On any structural change** (project added/removed/renamed, ProjectReference change): regenerate
  immediately and update `deps.json` plus affected module docs when the graph changes.
- **After changing a public API**: the per-project `symbols/<Proj>.md` regenerates wholesale;
  update the hot-seam digest in `symbols.md` only if a listed seam changed.
- **Quarterly**: re-run the Phase-1 audit (see `AUDIT.md` method) to find new >400-LOC token
  sinks and new undocumented-API pockets; refresh AUDIT.md.

## Known generator caveats (accept, don't fight)
- Purpose column = first `<summary>` in the file — occasionally a member's summary, not the type's.
- Multi-line signatures show only their first line; open the cited file:line for the rest.
- `[ObservableProperty]` fields generate public properties invisible to the extractor.
- Interface bodies ARE captured (state machine); default interface method bodies may add noise lines.

## Size discipline
- Keep `PROTOCOL.md` < 150 lines; split if it grows. Keep every context file small + greppable.
- Retain completed task records as durable history; archive or remove abandoned scratchpads only after review.
  Prune orphaned symbols only via regeneration (never hand-edit `symbols/`).

## Public context manager

`powershell -File .claude/context/manage-context.ps1 summary` gives a low-cost tree/project
overview. `... check` validates the dependency graph, exact index path coverage, and symbol
anchors. SessionStart runs this fast check and reports PASS, STALE, or BUSY/CHANGING. `... deep-check`
additionally runs the byte-for-byte Windows generator check. The Stop hook uses `gate-check`, which
serializes terminals, reuses only an exact successful fingerprint, defers a moving snapshot, and
still blocks deterministic stale context.
