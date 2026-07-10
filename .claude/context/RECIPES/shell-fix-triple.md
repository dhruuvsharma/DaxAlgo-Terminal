# RECIPE — shell fix ×3 (Basic + Intermediate here, Professional in the Pro repo)

The three shells are independent full copies (ADR-0002). A shell-code change is not done until
all three have it. The two local copies (fix BOTH, same edit):

| File | Basic | Intermediate |
|---|---|---|
| composition | `src/windows/Shell/TradingTerminal.App.Basic/Composition/AppDependencyInjection.cs` | `…/TradingTerminal.App.Intermediate/Composition/AppDependencyInjection.cs` |
| main window | `…App.Basic/MainWindow.xaml` (+ `.xaml.cs`) | `…App.Intermediate/MainWindow.xaml` (+ `.xaml.cs`) |
| main VM | `…App.Basic/MainWindowViewModel.cs` | `…App.Intermediate/MainWindowViewModel.cs` |
| startup | `…App.Basic/App.xaml.cs` | `…App.Intermediate/App.xaml.cs` |

Procedure:
1. Locate the site in ONE copy (grep `index/Shell.md` / `symbols/App.Basic.md`), apply the edit.
2. Port to the other copy — do NOT assume identical line numbers; re-grep. Intentional deltas:
   Basic = keyless brokers only (`AddKeylessBrokers()`, keyless login forms, no `DevLive` profile).
3. Verify drift: `git diff --no-index src/windows/Shell/TradingTerminal.App.Basic/<file> src/windows/Shell/TradingTerminal.App.Intermediate/<file>` — only edition-intentional differences remain.
4. Build BOTH: `dotnet build TradingTerminal.Windows.Basic.slnf` and `…Intermediate.slnf`.
5. Report the Pro copy for Dhruv: same edit in `TradingTerminal.App` in `D:\Github\DaxAlgo-Terminal-Pro`
   (never paste Pro code here). List the exact file + change in the final report.
