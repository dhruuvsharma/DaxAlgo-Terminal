# MAINTENANCE — keeping the context layer honest

## Regeneration (mechanical parts)
`bash .claude/context/gen-context.sh` from the repo root (Git Bash; several minutes) stages and
replaces `index/*.md` and `symbols/*.md`, pruning orphans, and reports a fresh `deps.tsv`.
Use `bash .claude/context/gen-context.sh --check` for a non-mutating byte-for-byte check. Hand-written files
(`index.md`, `symbols.md`, `deps.json`, `modules/`, `adr/`, `RECIPES/`, `PROTOCOL.md`,
`glossary.md`) are never touched by the script — update those by hand when the graph changes.

## When
- **Every ~10 sessions, or when index.md feels stale**: rerun `gen-context.sh`; if `deps.tsv`
  differs from `deps.json`, update deps.json + the module docs of the changed projects.
- **On any structural change** (project added/removed/renamed, ProjectReference change): rerun
  immediately — the `docsync-on-stop` hook fires on exactly these, use it as the reminder.
- **After changing a public API**: the per-project `symbols/<Proj>.md` regenerates wholesale;
  update the hot-seam digest in `symbols.md` only if a seam listed there changed.
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

## Linux tree (isolated lazy slice)
Linux/Avalonia context lives under `linux/` and is intentionally separate from the Windows
masters. Load `linux/index.md`, `linux/symbols.md`, and `linux/deps.json` only for Linux work.
Regenerate with `bash .claude/context/gen-context-linux.sh`; verify without writes using
`bash .claude/context/gen-context-linux.sh --check`. The generator stages and replaces only the
Linux subtree, indexes `.cs`/`.xaml`/`.axaml` under `src/linux` + `tests/linux`, removes orphaned
generated files, and leaves the existing Windows context untouched.

## Public context manager

`powershell -File .claude/context/manage-context.ps1 summary` gives a low-cost tree/project
overview. `... check` validates both dependency graphs, exact index path coverage, and symbol
anchors. `... deep-check` additionally runs byte-for-byte Windows and Linux generator checks; use
it after changing context machinery or generated artifacts, not on every session start.
