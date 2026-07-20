# Linux index / Tools

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeServiceCollectionExtensions.cs` | 21 | linux | TradingTerminal.AdvancedMarketRegime | product | Y | DI registration for the Advanced Live Market Regime dashboard, including the |
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml.cs` | 11 | linux | TradingTerminal.AdvancedMarketRegime | product | Y |  |
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml` | 251 | linux | TradingTerminal.AdvancedMarketRegime | product | N | UI |
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeViewModel.cs` | 360 | linux | TradingTerminal.AdvancedMarketRegime | product | Y | Rebuild the bindable header + row grid from the cached snapshot, applying |
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.AdvancedMarketRegime | product | Y | Avalonia (cross-platform) view for the Advanced Market Regime dashboard — net9.0-leg |
| `src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml` | 28 | linux | TradingTerminal.AdvancedMarketRegime | product | N | UI |
| `src/linux/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml.cs` | 58 | linux | TradingTerminal.Backtest | product | Y | Avalonia (cross-platform) view for the Backtest tool — net9.0-leg counterpart to the |
| `src/linux/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml` | 40 | linux | TradingTerminal.Backtest | product | N | UI |
| `src/linux/Tools/TradingTerminal.Backtest/BacktestServiceCollectionExtensions.cs` | 24 | linux | TradingTerminal.Backtest | product | Y | DI registration for the Backtest tab. |
| `src/linux/Tools/TradingTerminal.Backtest/BacktestView.xaml.cs` | 41 | linux | TradingTerminal.Backtest | product | Y |  |
| `src/linux/Tools/TradingTerminal.Backtest/BacktestView.xaml` | 188 | linux | TradingTerminal.Backtest | product | N | UI |
| `src/linux/Tools/TradingTerminal.Backtest/BacktestViewModel.cs` | 181 | linux | TradingTerminal.Backtest | product | Y | Raised after a run completes so the view can redraw the ScottPlot |
| `src/linux/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml.cs` | 37 | linux | TradingTerminal.Backtest | product | Y |  |
| `src/linux/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml` | 213 | linux | TradingTerminal.Backtest | product | N | UI |
| `src/linux/Tools/TradingTerminal.Backtest/QuickBacktestViewModel.cs` | 426 | linux | TradingTerminal.Backtest | product | Y | How the Quick-backtest sources its replay data. |
| `src/linux/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml.cs` | 12 | linux | TradingTerminal.BacktestStudio | product | Y | Avalonia (cross-platform) view for Backtest Studio — net9.0-leg counterpart to the WPF |
| `src/linux/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml` | 63 | linux | TradingTerminal.BacktestStudio | product | N | UI |
| `src/linux/Tools/TradingTerminal.BacktestStudio/AxisRowViewModel.cs` | 28 | linux | TradingTerminal.BacktestStudio | product | Y | One row in the optimization axis editor: a parameter the user can |
| `src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioServiceCollectionExtensions.cs` | 40 | linux | TradingTerminal.BacktestStudio | product | Y | DI registration for the Backtest Studio. Seeds the kernel registry from the |
| `src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml.cs` | 135 | linux | TradingTerminal.BacktestStudio | product | Y | Code-behind for the Studio. Pure view concern: it listens for the VM's |
| `src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml` | 361 | linux | TradingTerminal.BacktestStudio | product | N | UI |
| `src/linux/Tools/TradingTerminal.BacktestStudio/BacktestStudioViewModel.cs` | 533 | linux | TradingTerminal.BacktestStudio | product | Y | The last completed report — read by the view to draw the |
| `src/linux/Tools/TradingTerminal.BacktestStudio/DataSourceKind.cs` | 14 | linux | TradingTerminal.BacktestStudio | product | Y | Where the Studio pulls market data from for a run. |
| `src/linux/Tools/TradingTerminal.BacktestStudio/LegacyKernelDescriptors.cs` | 32 | linux | TradingTerminal.BacktestStudio | product | Y | Bridges the 12 legacy engine strategies (the catalog) into |
| `src/linux/Tools/TradingTerminal.BacktestStudio/ParamRowViewModel.cs` | 24 | linux | TradingTerminal.BacktestStudio | product | Y | One editable row in the parameter panel, generated from a kernel's |
| `src/linux/Tools/TradingTerminal.BacktestStudio/ParquetMarketDataFeed.cs` | 36 | linux | TradingTerminal.BacktestStudio | product | Y | A feed that replays a recorded parquet tick file through the new |
| `src/linux/Tools/TradingTerminal.BacktestStudio/TrialRowViewModel.cs` | 21 | linux | TradingTerminal.BacktestStudio | product | Y | A flattened optimization trial for the results grid — the parameter dictionary |
| `src/linux/Tools/TradingTerminal.BacktestStudio/WalkForwardRowViewModel.cs` | 25 | linux | TradingTerminal.BacktestStudio | product | Y | A walk-forward fold flattened for the results grid: the in-sample-chosen parameters and |
| `src/linux/Tools/TradingTerminal.Correlation/AvaloniaUi/LiveCorrelationAvaloniaWindow.axaml.cs` | 57 | linux | TradingTerminal.Correlation | product | Y | Avalonia (cross-platform) view for the Live Correlation Matrix — net9.0-leg counterpart to |
| `src/linux/Tools/TradingTerminal.Correlation/AvaloniaUi/LiveCorrelationAvaloniaWindow.axaml` | 48 | linux | TradingTerminal.Correlation | product | N | UI |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixControl.cs` | 189 | linux | TradingTerminal.Correlation | product | Y | Single source of the diverging red/grey/green heat colours (cached, frozen). |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixViewModel.cs` | 243 | linux | TradingTerminal.Correlation | product | Y | Per-instrument fetch outcome. |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml.cs` | 28 | linux | TradingTerminal.Correlation | product | Y | Standalone window hosting the Correlation Matrix tool. Pure view — all behaviour |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml` | 257 | linux | TradingTerminal.Correlation | product | N | UI |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationPickerViewModelBase.cs` | 340 | linux | TradingTerminal.Correlation | product | Y | Hard cap on how many rows the checklist shows at once. A |
| `src/linux/Tools/TradingTerminal.Correlation/CorrelationServiceCollectionExtensions.cs` | 19 | linux | TradingTerminal.Correlation | product | Y | DI registration for the Correlation Matrix tools (historical + live). Transient so |
| `src/linux/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixViewModel.cs` | 246 | linux | TradingTerminal.Correlation | product | Y | Changing the cadence live just re-paces the running sampler; the rolling window |
| `src/linux/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml.cs` | 28 | linux | TradingTerminal.Correlation | product | Y | Standalone window hosting the Live Correlation Matrix tool. Pure view — all |
| `src/linux/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml` | 250 | linux | TradingTerminal.Correlation | product | N | UI |
| `src/linux/Tools/TradingTerminal.LseBacktest/AvaloniaUi/LseBacktestAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.LseBacktest | product | Y | Avalonia (cross-platform) view for the LSE backtester — net9.0-leg counterpart to the |
| `src/linux/Tools/TradingTerminal.LseBacktest/AvaloniaUi/LseBacktestAvaloniaWindow.axaml` | 36 | linux | TradingTerminal.LseBacktest | product | N | UI |
| `src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestServiceCollectionExtensions.cs` | 22 | linux | TradingTerminal.LseBacktest | product | Y | DI registration for the LSE Tools -> LSE backtester window. Shares the |
| `src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestView.xaml.cs` | 41 | linux | TradingTerminal.LseBacktest | product | Y |  |
| `src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestView.xaml` | 176 | linux | TradingTerminal.LseBacktest | product | N | UI |
| `src/linux/Tools/TradingTerminal.LseBacktest/LseBacktestViewModel.cs` | 213 | linux | TradingTerminal.LseBacktest | product | Y | Raised after a run completes so the view can redraw the ScottPlot |
| `src/linux/Tools/TradingTerminal.QuantConnect/AvaloniaUi/QuantConnectAvaloniaWindow.axaml.cs` | 56 | linux | TradingTerminal.QuantConnect | product | Y | Avalonia (cross-platform) view for the QuantConnect / LEAN tool — net9.0-leg counterpart |
| `src/linux/Tools/TradingTerminal.QuantConnect/AvaloniaUi/QuantConnectAvaloniaWindow.axaml` | 89 | linux | TradingTerminal.QuantConnect | product | N | UI |
| `src/linux/Tools/TradingTerminal.QuantConnect/LeanProcessRunner.cs` | 89 | linux | TradingTerminal.QuantConnect | product | Y | Outcome of a subprocess run: exit code (null = could not start |
| `src/linux/Tools/TradingTerminal.QuantConnect/LeanRuntimeSettings.cs` | 18 | linux | TradingTerminal.QuantConnect | product | Y | Mutable, process-wide LEAN settings shared by the client and the Settings panel. |
| `src/linux/Tools/TradingTerminal.QuantConnect/LocalCliLeanClient.cs` | 225 | linux | TradingTerminal.QuantConnect | product | Y | Locates the newest |
| `src/linux/Tools/TradingTerminal.QuantConnect/NullLeanClient.cs` | 36 | linux | TradingTerminal.QuantConnect | product | Y | No-op client used when an engine mode isn't wired yet (currently ). |
| `src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectServiceCollectionExtensions.cs` | 50 | linux | TradingTerminal.QuantConnect | product | Y | DI registration for the QuantConnect / LEAN tool. Binds , seeds the |
| `src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectViewModel.cs` | 255 | linux | TradingTerminal.QuantConnect | product | Y | 0=Backtest, 1=Projects, 2=Data, 3=Settings — driven by the menu deep-links. |
| `src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectWindow.xaml.cs` | 17 | linux | TradingTerminal.QuantConnect | product | Y | View for the QuantConnect / LEAN tool window. Pure view concerns only |
| `src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectWindow.xaml` | 299 | linux | TradingTerminal.QuantConnect | product | N | UI |
| `src/linux/Tools/TradingTerminal.Recording/AvaloniaUi/TickRecorderAvaloniaWindow.axaml.cs` | 10 | linux | TradingTerminal.Recording | product | Y | Avalonia (cross-platform) view for the live tick recorder — net9.0-leg counterpart to |
| `src/linux/Tools/TradingTerminal.Recording/AvaloniaUi/TickRecorderAvaloniaWindow.axaml` | 38 | linux | TradingTerminal.Recording | product | N | UI |
| `src/linux/Tools/TradingTerminal.Recording/RecordingServiceCollectionExtensions.cs` | 16 | linux | TradingTerminal.Recording | product | Y | DI registration for the live tick recorder tab. |
| `src/linux/Tools/TradingTerminal.Recording/TickRecorderView.xaml.cs` | 8 | linux | TradingTerminal.Recording | product | Y |  |
| `src/linux/Tools/TradingTerminal.Recording/TickRecorderView.xaml` | 124 | linux | TradingTerminal.Recording | product | N | UI |
| `src/linux/Tools/TradingTerminal.Recording/TickRecorderViewModel.cs` | 172 | linux | TradingTerminal.Recording | product | Y | Live tick recorder. Subscribes to |
