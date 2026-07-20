# TradingTerminal.Settings — settings window

**Path** `src/windows/UI/TradingTerminal.Settings/` · **Editions** B I P · **Blast: med**

**Purpose.** The Settings window: store backend selection, notifications, research/sidecar toggles,
archive options, dev options. Registered via `AddSettingsSurface` (called from each shell's
`Composition/AppDependencyInjection.cs`).

**Depends on** Core, Infrastructure, UI.Core. **Depended by** both shells.
**Surface** `symbols/Settings.md`. **Tests** Tests.Headless `~Settings`.

**Common changes.** New settings section when a subsystem gains options — bind to the options
record in Core, persist via the settings store, and wire both public edition shells when shared.
Any consuming overlay follows its own guide. Strict MVVM; no business logic in the window.
