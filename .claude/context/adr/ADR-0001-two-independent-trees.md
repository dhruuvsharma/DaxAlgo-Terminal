# ADR-0001 — Two independent trees, zero shared code

**Date** 2026-06-27 · **Status** accepted

**Context.** The Linux/Avalonia port initially multi-targeted shared projects; Linux work kept
destabilizing the Windows/WPF build.

**Decision.** Fork into two fully independent codebases: `src/windows/` +
`TradingTerminal.Windows.slnx` (WPF, net9.0-windows7.0) and `src/linux/` +
`TradingTerminal.Linux.slnx` (Avalonia, net9.0). `src/shared/` deleted; no multi-targeting.
Namespaces are intentionally identical — the trees never compile together.

**Consequences.** A backend fix that applies to both is made TWICE (see
`RECIPES/cross-tree-fix.md`). Windows-only projects exist only under `src/windows/`. Never run
bare `dotnet build` (two solutions). Context layer indexes Windows only as of 2026-07-10.
