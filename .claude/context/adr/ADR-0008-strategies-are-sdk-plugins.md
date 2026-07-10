# ADR-0008 — Strategies are SDK plugins, runtime-loaded (not shell references)

**Date** rolled out by 2026-07-10 (plugin-marketplace initiative) · **Status** accepted

**Context.** The plugin marketplace needs strategies installable/updatable without rebuilding
the shell, with signing/trust, and without strategy code referencing app internals.

**Decision.** Every strategy project references ONLY `DaxAlgo.Sdk.Wpf` (a zero-source facade
bundling `DaxAlgo.Sdk` + `UI` + `UI.Core`; SDK is MIT). Each ships `<Name>Plugin :
IStrategyPlugin` + `Add<Name>Strategy()`. Shells do NOT ProjectReference strategies; each
shell's `Composition/AppDependencyInjection.cs` `AddStrategyPlugins()` loads them at runtime
via `Infrastructure/Plugins/PluginLoader` (AssemblyLoadContext, `plugin.json` manifest,
Authenticode `PluginTrustPolicy`).

**Consequences.** CLAUDE.md's older graph (`Strategies.* → Infrastructure, UI, Core`) is
superseded. Adding a strategy = new plugin project + SDK surface only
(`RECIPES/add-strategy.md`); breaking the SDK surface breaks every installed plugin —
version-gate via `PluginLoader.IsCompatible`.
