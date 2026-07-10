# MAINTENANCE — keeping the context layer honest

## Regeneration (mechanical parts)
`bash .claude/context/gen-context.sh` from the repo root (Git Bash; ~2 min) rebuilds
`index/*.md`, `symbols/*.md`, and a fresh `deps.tsv` in a temp dir. Hand-written files
(`index.md`, `symbols.md`, `deps.json`, `modules/`, `adr/`, `RECIPES/`, `PROTOCOL.md`,
`glossary.md`) are never touched by the script — update those by hand when the graph changes.

## When
- **Every ~10 sessions, or when index.md feels stale**: rerun gen-context.sh; if `deps.tsv`
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
- Prune `tasks/` entries older than a month. Prune orphaned symbols only via regeneration (never hand-edit `symbols/`).

## Extending to the Linux tree (not done — Windows-only per 2026-07-10 scope)
Clone the three path lists in gen-context.sh (`src/windows` → `src/linux`, tests →
`tests/linux`), emit into `index-linux/` + `symbols-linux/`, add a `"tree": "linux"` block to
deps.json. Do NOT merge trees into one file — they are independent codebases.
