# ADR-0013 — Linux edition extracted to a private repository

**Date** 2026-07-22 · **Status** accepted

**Context.** Keeping independent Windows/WPF and Linux/Avalonia implementations in one repository
made every Windows task pay a routing, context, and parity cost despite zero shared source.

**Decision.** The Linux/Avalonia source, tests, solution, build helpers, and generated context moved
to a standalone private repository. This repository now owns only the Windows/WPF implementation.

**Consequences.** Codex loads one Windows context layer here. Windows changes have no Linux mirror
obligation. The private Linux repository is inspected or coordinated only when the user explicitly
opens it and expands scope. Historical commits retain the pre-extraction Linux source.
