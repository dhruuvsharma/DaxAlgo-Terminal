# RECIPE — shared shell fix (Basic + Intermediate)

The two public Windows edition shells are independent copies. When behavior is shared, update both
without erasing their intentional composition differences.

| Concern | Basic | Intermediate |
|---|---|---|
| composition | `src/windows/Shell/TradingTerminal.App.Basic/Composition/` | `src/windows/Shell/TradingTerminal.App.Intermediate/Composition/` |
| main window | `src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml` | `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindow.xaml` |
| main VM | `src/windows/Shell/TradingTerminal.App.Basic/MainWindowViewModel.cs` | `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindowViewModel.cs` |
| startup | `src/windows/Shell/TradingTerminal.App.Basic/App.xaml.cs` | `src/windows/Shell/TradingTerminal.App.Intermediate/App.xaml.cs` |

1. Locate the exact site through `index/Shell.md` and the matching symbol shard.
2. Apply the change to one edition, then port the intent—not line numbers—to the other.
3. Preserve edition-specific registration: Basic remains keyless; Intermediate includes its
   credentialed composition and profiles.
4. Compare the corresponding files and review every remaining difference as intentional.
5. Build `TradingTerminal.Windows.Basic.slnf` and
   `TradingTerminal.Windows.Intermediate.slnf`; run focused WPF tests when applicable.

When this public repository is consumed by an overlay, that overlay's own guide decides whether a
third shell copy must also change. Never paste private implementation into this tree.
