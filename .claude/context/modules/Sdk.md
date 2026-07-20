# DaxAlgo.Sdk / DaxAlgo.Sdk.Wpf / DaxAlgo.SamplePlugin — plugin SDK (MIT)

**Paths** `src/windows/Sdk/DaxAlgo.Sdk/` , `src/windows/Sdk/DaxAlgo.Sdk.Wpf/`
(**zero source — facade csproj** bundling Sdk + UI + UI.Core), `samples/DaxAlgo.SamplePlugin/`
(reference plugin) · **Editions** B I P (SamplePlugin dev-only) · **Blast: HIGH via Sdk.Wpf (installed UI strategy plugins)**

**Purpose.** The strategy-plugin contract (ADR-0008): `IStrategyPlugin`
(`DaxAlgo.Sdk/IStrategyPlugin.cs`) + version compatibility. Strategy projects reference
Sdk.Wpf ONLY; the loader (`Infrastructure/Plugins/PluginLoader.cs`) enforces
`IsCompatible(pluginVersion, hostVersion)`, `plugin.json` manifest, Authenticode trust policy.

**License boundary.** Sdk stays MIT while the repo is AGPL — keep GPL'd code out of Sdk projects.

**Surface** `symbols/DaxAlgo.Sdk.md`, `symbols/DaxAlgo.SamplePlugin.md`.
**Tests** Tests.Headless (loads SamplePlugin as fixture).

**Common changes.** SDK surface additions must be backward-compatible or version-gated —
a break invalidates every installed plugin. Adding a type to the plugin-visible surface usually
means re-exporting via Sdk.Wpf's references, not new code here.
