# TradingTerminal.App.Basic — Basic edition shell (keyless)

**Path** `src/windows/Shell/TradingTerminal.App.Basic/` · 4,545 LOC / 40 files · **Edition** Basic only · **Blast: low (leaf) but ×3 rule**

**Purpose.** Independent WinExe shell (RootNamespace `TradingTerminal.App`): MainWindow strategy
catalog + activity-log drawer, inline menus, DI composition, CrashGuard, notifications/archive UI.
**Keyless brokers only**: calls `AddKeylessBrokers()` (never `AddCredentialedBrokers()` /
`AddCredentialedLoginForms()`), keyless login forms, no `DevLive` profile.

**Key files.** `Composition/AppDependencyInjection.cs` (AddStrategyPlugins:105,
AddSettingsSurface:213, AddArchiveSurface:226) · `MainWindow.xaml` (830) ·
`MainWindowViewModel.cs` (632) · `App.xaml.cs` · `Properties/launchSettings.json` (DevSim/DevReplay).

**THE RULE.** Shell code is copied ×3 — any fix here must also land in App.Intermediate and the
Pro shell: `RECIPES/shell-fix-triple.md`. Surface: `symbols/App.Basic.md`.

**Build** `dotnet build TradingTerminal.Windows.Basic.slnf` · **Run**
`dotnet run --project src/windows/Shell/TradingTerminal.App.Basic`.
**Common changes.** Menu/DI wiring for a new tool (reference + `Add…Surface` call), catalog UX,
edition gating (composition-only — never `#if`).
