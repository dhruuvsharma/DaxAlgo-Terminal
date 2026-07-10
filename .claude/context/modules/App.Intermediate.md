# TradingTerminal.App.Intermediate — Intermediate edition shell (all brokers)

**Path** `src/windows/Shell/TradingTerminal.App.Intermediate/` · 4,562 LOC / 40 files · **Edition** Intermediate only · **Blast: low (leaf) but ×3 rule**

**Purpose.** Independent WinExe shell (RootNamespace `TradingTerminal.App`), same tool surface as
Basic but **all 12 brokers + full login**: `AddCredentialedBrokers()` + `AddKeylessBrokers()` +
`AddCredentialedLoginForms()` paired (unpaired forms crash the login window). Dev profiles include
`DevLive`. This is Dhruv's default working edition.

**Key files.** `Composition/AppDependencyInjection.cs` (AddStrategyPlugins:105,
AddSettingsSurface:213, AddArchiveSurface:226) · `MainWindow.xaml` (831) ·
`MainWindowViewModel.cs` (632) · `App.xaml.cs` · `Properties/launchSettings.json`.

**THE RULE.** Copied ×3 — fixes also land in App.Basic + the Pro shell:
`RECIPES/shell-fix-triple.md`. Surface: `symbols/App.Intermediate.md`.

**Build** `dotnet build TradingTerminal.Windows.Intermediate.slnf` (the default narrow filter) ·
**Run** `dotnet run --project src/windows/Shell/TradingTerminal.App.Intermediate`.
**Common changes.** Same classes of change as Basic; diff-check the pair after every edit
(`git diff --no-index` per the recipe).
