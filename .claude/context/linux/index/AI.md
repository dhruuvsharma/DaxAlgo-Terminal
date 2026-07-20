# Linux index / AI

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/AI/TradingTerminal.Ai/Analyst/AiAnalystEnricher.cs` | 111 | linux | TradingTerminal.Ai | product | Y | Notification enricher that appends a one-line AI Analyst verdict to every signal |
| `src/linux/AI/TradingTerminal.Ai/Analyst/AiAnalystServiceCollectionExtensions.cs` | 63 | linux | TradingTerminal.Ai | product | Y | Registers the AI Analyst seam. The single registered |
| `src/linux/AI/TradingTerminal.Ai/Analyst/HttpAiAnalystClient.cs` | 154 | linux | TradingTerminal.Ai | product | Y | HTTP client for the Python daxalgo-ml sidecar's /analyst/run endpoint. |
| `src/linux/AI/TradingTerminal.Ai/Analyst/NullAiAnalystClient.cs` | 17 | linux | TradingTerminal.Ai | product | Y | Stand-in registered when AiAnalystOptions.Enabled is false (no Python sidecar |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/AvaloniaUi/BacktestAnalysisAvaloniaWindow.axaml.cs` | 10 | linux | TradingTerminal.Ai.BacktestAnalysis | product | Y | Avalonia (cross-platform) view for the Backtest Analysis tool — net9.0-leg counterpart to |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/AvaloniaUi/BacktestAnalysisAvaloniaWindow.axaml` | 56 | linux | TradingTerminal.Ai.BacktestAnalysis | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisServiceCollectionExtensions.cs` | 16 | linux | TradingTerminal.Ai.BacktestAnalysis | product | Y | DI registration for the backtest analysis tab (walk-forward + Monte-Carlo). |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisView.xaml.cs` | 11 | linux | TradingTerminal.Ai.BacktestAnalysis | product | Y |  |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisView.xaml` | 217 | linux | TradingTerminal.Ai.BacktestAnalysis | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.BacktestAnalysis/BacktestAnalysisViewModel.cs` | 291 | linux | TradingTerminal.Ai.BacktestAnalysis | product | Y | Backtest analysis tab: combines two pre-deployment diagnostics every quant runs before |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/AvaloniaUi/FactorResearchAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.Ai.FactorResearch | product | Y | Avalonia (cross-platform) view for the Factor Research tool — net9.0-leg counterpart to |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/AvaloniaUi/FactorResearchAvaloniaWindow.axaml` | 34 | linux | TradingTerminal.Ai.FactorResearch | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchServiceCollectionExtensions.cs` | 16 | linux | TradingTerminal.Ai.FactorResearch | product | Y | DI registration for the factor research notebook tab. |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchView.xaml.cs` | 8 | linux | TradingTerminal.Ai.FactorResearch | product | Y |  |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchView.xaml` | 137 | linux | TradingTerminal.Ai.FactorResearch | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.FactorResearch/FactorResearchViewModel.cs` | 125 | linux | TradingTerminal.Ai.FactorResearch | product | Y | Factor research tab. Loads a parquet tick file (from the live recorder |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AiAnalystView.xaml.cs` | 11 | linux | TradingTerminal.Ai.MarketAnalyst | product | Y |  |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AiAnalystView.xaml` | 228 | linux | TradingTerminal.Ai.MarketAnalyst | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AiAnalystViewModel.cs` | 185 | linux | TradingTerminal.Ai.MarketAnalyst | product | Y | View-model for the AI Market Analyst dock pane. Fetches a window of |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AvaloniaUi/AiAnalystAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.Ai.MarketAnalyst | product | Y | Avalonia (cross-platform) view for the AI Market Analyst — net9.0-leg counterpart to |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/AvaloniaUi/AiAnalystAvaloniaWindow.axaml` | 53 | linux | TradingTerminal.Ai.MarketAnalyst | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.MarketAnalyst/MarketAnalystServiceCollectionExtensions.cs` | 17 | linux | TradingTerminal.Ai.MarketAnalyst | product | Y | DI registration for the AI Market Analyst dock pane. The analyst client |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/AvaloniaUi/MlFeaturesAvaloniaWindow.axaml.cs` | 10 | linux | TradingTerminal.Ai.MlFeatures | product | Y | Avalonia (cross-platform) view for the ML Features tool — net9.0-leg counterpart to |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/AvaloniaUi/MlFeaturesAvaloniaWindow.axaml` | 39 | linux | TradingTerminal.Ai.MlFeatures | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesServiceCollectionExtensions.cs` | 16 | linux | TradingTerminal.Ai.MlFeatures | product | Y | DI registration for the ML features tab (triple-barrier labelling + feature export). |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesView.xaml.cs` | 11 | linux | TradingTerminal.Ai.MlFeatures | product | Y |  |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesView.xaml` | 135 | linux | TradingTerminal.Ai.MlFeatures | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.MlFeatures/MlFeaturesViewModel.cs` | 189 | linux | TradingTerminal.Ai.MlFeatures | product | Y | ML Features tab. Loads a parquet tick file, aggregates into N-tick bars |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/AvaloniaUi/PaperLabAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.Ai.PaperLab | product | Y | Avalonia (cross-platform) view for Paper Lab — net9.0-leg counterpart to the WPF |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/AvaloniaUi/PaperLabAvaloniaWindow.axaml` | 53 | linux | TradingTerminal.Ai.PaperLab | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabServiceCollectionExtensions.cs` | 24 | linux | TradingTerminal.Ai.PaperLab | product | Y | Register the Paper Lab view and view-model as transient services. Mirrors |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabView.xaml.cs` | 13 | linux | TradingTerminal.Ai.PaperLab | product | Y | Code-behind for |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabView.xaml` | 369 | linux | TradingTerminal.Ai.PaperLab | product | N | UI |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/PaperLabViewModel.cs` | 476 | linux | TradingTerminal.Ai.PaperLab | product | Y | True when any async operation is in-flight. Drives the progress ring. |
| `src/linux/AI/TradingTerminal.Ai.PaperLab/ReproJobRowViewModel.cs` | 113 | linux | TradingTerminal.Ai.PaperLab | product | Y | Apply a fresh job snapshot, updating only the fields that may have |
