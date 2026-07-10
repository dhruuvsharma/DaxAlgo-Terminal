# ADR-0002 — Three edition shells, no shared shell library

**Date** 2026-07-08 (#19) · **Status** accepted

**Context.** Basic / Intermediate / Professional editions need different broker + tool bundles;
a shared App.Core shell library was rejected to keep editions independently shippable and the
open-core boundary clean.

**Decision.** Three fully independent WinExe shells, each owning a complete copy of the shell
code (`RootNamespace` stays `TradingTerminal.App` in all three): `TradingTerminal.App.Basic` and
`TradingTerminal.App.Intermediate` here under `src/windows/Shell/`; `TradingTerminal.App`
(Professional) in the private Pro repo. Tier gating is composition-only (`AddKeylessBrokers()`
vs `AddCredentialedBrokers()`, login-form registration, project references).

**Consequences.** Every shell-code fix is applied ×3 (`RECIPES/shell-fix-triple.md`). Basic never
references Pro-only projects. `AddCredentialedLoginForms()` must always pair with
`AddCredentialedBrokers()` or the login window crashes. Per-edition `.slnf` filters at repo root.
