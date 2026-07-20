# Linux index / Shell

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/Shell/TradingTerminal.App.Avalonia/App.axaml.cs` | 108 | linux | TradingTerminal.App.Avalonia | product | Y | The composed DI graph; views resolve ported per-strategy VMs from here. |
| `src/linux/Shell/TradingTerminal.App.Avalonia/App.axaml` | 16 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/AvaloniaUiDispatcher.cs` | 19 | linux | TradingTerminal.App.Avalonia | product | Y | backed by Avalonia's UI-thread dispatcher. Registered in the Avalonia |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Charts/ChartsViewModel.cs` | 45 | linux | TradingTerminal.App.Avalonia | product | Y | Synthetic OHLC bars: parallel arrays so the view stays free of ScottPlot |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Charts/ChartsWindow.axaml.cs` | 53 | linux | TradingTerminal.App.Avalonia | product | Y | Cross-platform Charts window — ScottPlot.Avalonia candlestick chart that replaces the Windows-only |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Charts/ChartsWindow.axaml` | 19 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Charts/LineChartControl.cs` | 89 | linux | TradingTerminal.App.Avalonia | product | Y | Optional overlay series (cyan), e.g. a filtered/forecast trace over the raw series. |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Composition/ServiceConfiguration.cs` | 142 | linux | TradingTerminal.App.Avalonia | product | Y | Composition root for the Avalonia shell. Mirrors the WPF App's MS.DI host |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Login/LoginViewModel.cs` | 76 | linux | TradingTerminal.App.Avalonia | product | Y | One selectable broker row on the login screen. |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Login/LoginWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Login/LoginWindow.axaml` | 57 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchViewModel.cs` | 63 | linux | TradingTerminal.App.Avalonia | product | Y | Avalonia "ARIMA &amp; GARCH" window VM. Fits the broker-neutral Core |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchWindow.axaml` | 48 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanViewModel.cs` | 60 | linux | TradingTerminal.App.Avalonia | product | Y | Avalonia "Kalman Filter" window VM. Runs the broker-neutral Core |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanWindow.axaml` | 41 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityViewModel.cs` | 61 | linux | TradingTerminal.App.Avalonia | product | Y | Avalonia "Stationarity &amp; Differencing" window VM. Runs the broker-neutral Core time-series |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityWindow.axaml` | 48 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Program.cs` | 16 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveActivityWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveActivityWindow.axaml` | 23 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveSettingsWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveSettingsWindow.axaml` | 43 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/NotificationsSettingsWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/NotificationsSettingsWindow.axaml` | 28 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ResearchSettingsWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ResearchSettingsWindow.axaml` | 24 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml` | 28 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/SupportWindow.axaml.cs` | 18 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/SupportWindow.axaml` | 20 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiChipViewModel.cs` | 84 | linux | TradingTerminal.App.Avalonia | product | Y | Drives the chip's background colour bucket. |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiMeterViewModel.cs` | 72 | linux | TradingTerminal.App.Avalonia | product | Y | Header-strip API meter — one chip per broker being talked to. Avalonia |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindow.axaml.cs` | 313 | linux | TradingTerminal.App.Avalonia | product | Y | Shows a tool/strategy window and — matching the WPF shell — disposes |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindow.axaml` | 376 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindowViewModel.cs` | 191 | linux | TradingTerminal.App.Avalonia | product | Y | Design-time ctor — empty graph so the previewer has something to render. |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/ShellConverters.cs` | 115 | linux | TradingTerminal.App.Avalonia | product | Y | Shell colour converters — Avalonia has no WPF-style DataTriggers, so the status |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Shell/StrategyPillConverters.cs` | 142 | linux | TradingTerminal.App.Avalonia | product | Y | One coloured catalog pill: label + background/foreground brushes. Avalonia mirror of the |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Themes/Controls.axaml` | 121 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Themes/Palette.axaml` | 82 | linux | TradingTerminal.App.Avalonia | product | N | UI |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationHeatmapControl.cs` | 110 | linux | TradingTerminal.App.Avalonia | product | Y | Custom-drawn correlation-matrix heatmap for Avalonia — an N×N grid coloured by Pearson |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationViewModel.cs` | 56 | linux | TradingTerminal.App.Avalonia | product | Y | Avalonia Correlation-matrix window VM. Computes a Pearson correlation matrix via the broker-neutral |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationWindow.axaml.cs` | 8 | linux | TradingTerminal.App.Avalonia | product | Y |  |
| `src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationWindow.axaml` | 30 | linux | TradingTerminal.App.Avalonia | product | N | UI |
