# TradingTerminal.Settings — settings window

**Path** `src/windows/UI/TradingTerminal.Settings/` · 1,290 LOC / 11 files · **Editions** B I P · **Blast: med**

**Purpose.** The Settings window: store backend selection, notifications, research/sidecar toggles,
archive options, dev options. Registered via `AddSettingsSurface()` (called from each shell's
`Composition/AppDependencyInjection.cs:213`).

**Depends on** Core, Infrastructure, UI.Core. **Depended by** both shells.
**Surface** `symbols/Settings.md`. **Tests** Tests.Headless `~Settings`.

**Common changes.** New settings section when a subsystem gains options — bind to the options
record in Core, persist via the settings store, and remember the SAME section must exist in the
Pro shell's settings (flag for Dhruv). Strict MVVM; no business logic in the window.
