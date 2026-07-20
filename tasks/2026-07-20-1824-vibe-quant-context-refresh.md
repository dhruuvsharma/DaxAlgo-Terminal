# Vibe Quant and context refresh

Status: complete

## Goal

Record and validate the pending shared Windows changes before publication: the Vibe Quant authoring
workspace, plugin-consent startup correction, repository guidance, and generated context refresh.

## Non-goals

- No Linux application behavior changes.
- No private-overlay code, identifiers, assets, or verification details in the public repository.
- No live order execution or first-party strategy project references.

## Evidence and affected areas

- Shared UI, Settings view-models, and Basic/Intermediate Windows shells contain the authoring work.
- Basic and Intermediate startup paths contain the plugin-consent owner correction.
- `.claude/`, `.codex/`, `AGENTS.md`, and task guidance contain the repository/context refresh.
- Obsolete in-tree strategy context is removed because strategies remain external runtime plugins.

## Decisions and blast radius

- Keep public Windows changes edition-neutral and preserve Linux as an independent tree.
- Treat shell changes as two public copies and verify both solution filters.
- Context changes affect navigation and generated facts, so generator checks are required.

## Validation plan

- Build both public Windows solution filters.
- Run the affected shared WPF tests.
- Run context structural and generator checks, structured-data parsing, and `git diff --check`.
- Audit staged content for credentials, oversized files, and private-overlay leakage.

## Closeout

- Both public Windows solution filters build successfully; each reports six existing XML-doc
  warnings and no errors.
- Focused shared WPF tests pass: 24/24.
- Public structural context check and both Windows/Linux generator checks pass.
- Changed JSON, TOML, XML/XAML, PowerShell, and Bash files parse successfully.
- Memory-safety, architecture, context-sync, credential, file-size, and public-boundary audits pass.
- `git diff --check` passes. The active app-shell guide now routes to
  `RECIPES/shell-fix-editions.md`; a historical audit keeps its dated retired-recipe observation.
- Final diff: shared authoring UI/view-model work, Basic and Intermediate startup correction,
  context/Codex guidance refresh, and removal of obsolete in-tree strategy context.
