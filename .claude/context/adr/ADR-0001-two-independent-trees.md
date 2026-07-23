# ADR-0001 — Two independent trees, zero shared code

**Date** 2026-06-27 · **Status** superseded by ADR-0013

**Context.** The Linux/Avalonia port initially multi-targeted shared projects; Linux work kept
destabilizing the Windows/WPF build.

**Decision.** Fork into two fully independent codebases: `src/windows/` +
`TradingTerminal.Windows.slnx` (WPF, net9.0-windows7.0) and `src/linux/` +
`TradingTerminal.Linux.slnx` (Avalonia, net9.0). `src/shared/` deleted; no multi-targeting.
Namespaces are intentionally identical — the trees never compile together.

**Consequences.** This decision governed the former combined repository. ADR-0013 moved the
Linux/Avalonia tree to an independent private repository; this repository now owns Windows only.
