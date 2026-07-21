# index/Tools — per-file index (Windows tree)

Generated from the current source tree. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Tools/DaxAlgo.Codegen/AgentCliCodegenClient.cs` | 347 | win | DaxAlgo.Codegen | B I P | Y | Per-CLI details, isolated so one vendor's output-format drift doesn't touch the others. |
| `src/windows/Tools/DaxAlgo.Codegen/AiModelCatalog.cs` | 60 | win | DaxAlgo.Codegen | B I P | Y | Anthropic model ids — the same strings Claude Code's |
| `src/windows/Tools/DaxAlgo.Codegen/AiStrategyBuilder.cs` | 106 | win | DaxAlgo.Codegen | B I P | Y | Every provider the app knows how to build, available or not — |
| `src/windows/Tools/DaxAlgo.Codegen/AnthropicCodegenClient.cs` | 288 | win | DaxAlgo.Codegen | B I P | Y | The models this key can actually call. A failure here is not |
| `src/windows/Tools/DaxAlgo.Codegen/AnthropicStreamParser.cs` | 111 | win | DaxAlgo.Codegen | B I P | Y | Everything the model has written so far. |
| `src/windows/Tools/DaxAlgo.Codegen/CliWorkspaceLauncher.cs` | 345 | win | DaxAlgo.Codegen | B I P | Y | What |
| `src/windows/Tools/DaxAlgo.Codegen/CodegenCodeExtractor.cs` | 148 | win | DaxAlgo.Codegen | B I P | Y | A bare file name mentioned in prose/info strings — |
| `src/windows/Tools/DaxAlgo.Codegen/FakeCodegenClient.cs` | 76 | win | DaxAlgo.Codegen | B I P | Y | How many times the loop asked this client to generate — the |
| `src/windows/Tools/DaxAlgo.Codegen/OpenAiCompatibleCodegenClient.cs` | 275 | win | DaxAlgo.Codegen | B I P | Y | Every OpenAI-compatible endpoint (including Ollama) exposes |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyBacktestSmoke.cs` | 112 | win | DaxAlgo.Codegen | B I P | Y | Ticks fed through |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyBuildSession.cs` | 469 | win | DaxAlgo.Codegen | B I P | Y | What one turn of the conversation produced. |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenClientFactory.cs` | 154 | win | DaxAlgo.Codegen | B I P | Y | Every provider the app knows how to build — installed agent CLIs, |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenOrchestrator.cs` | 80 | win | DaxAlgo.Codegen | B I P | Y | The result of a one-shot build: whether it produced a compiling strategy, |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenServiceCollectionExtensions.cs` | 56 | win | DaxAlgo.Codegen | B I P | Y | Wires the AI Strategy Builder into DI. Called once per shell from |
| `src/windows/Tools/DaxAlgo.Codegen/StrategyContextPack.cs` | 31 | win | DaxAlgo.Codegen | B I P | Y | The pack text — the codegen system prompt. |
| `src/windows/Tools/DaxAlgo.Codegen/StrategySkillLibrary.cs` | 163 | win | DaxAlgo.Codegen | B I P | Y | One on-demand domain pack: what it knows, and the words that mean |
| `src/windows/Tools/DaxAlgo.Strategy.BundleTool/Program.cs` | 772 | win | DaxAlgo.Strategy.BundleTool | B I P | Y |  |
| `src/windows/Tools/DaxAlgo.StrategyTool/ProcessRunner.cs` | 64 | win | DaxAlgo.StrategyTool | B I P | Y | Thin subprocess helper — runs a command, streams its output to the |
| `src/windows/Tools/DaxAlgo.StrategyTool/Program.cs` | 237 | win | DaxAlgo.StrategyTool | B I P | N |  |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeServiceCollectionExtensions.cs` | 21 | win | TradingTerminal.AdvancedMarketRegime | B I P | Y | DI registration for the Advanced Live Market Regime dashboard, including the |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml.cs` | 19 | win | TradingTerminal.AdvancedMarketRegime | B I P | Y |  |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml` | 284 | win | TradingTerminal.AdvancedMarketRegime | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeViewModel.cs` | 476 | win | TradingTerminal.AdvancedMarketRegime | B I P | Y | Key under which this window remembers the last selected instrument (see |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml.cs` | 11 | win | TradingTerminal.AdvancedMarketRegime | B I P | Y | Avalonia (cross-platform) view for the Advanced Market Regime dashboard — net9.0-leg |
| `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml` | 28 | win | TradingTerminal.AdvancedMarketRegime | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml.cs` | 58 | win | TradingTerminal.Backtest | B I P | Y | Avalonia (cross-platform) view for the Backtest tool — net9.0-leg counterpart to the |
| `src/windows/Tools/TradingTerminal.Backtest/AvaloniaUi/BacktestAvaloniaWindow.axaml` | 40 | win | TradingTerminal.Backtest | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Backtest/BacktestServiceCollectionExtensions.cs` | 24 | win | TradingTerminal.Backtest | B I P | Y | DI registration for the Backtest tab. |
| `src/windows/Tools/TradingTerminal.Backtest/BacktestView.xaml.cs` | 49 | win | TradingTerminal.Backtest | B I P | Y |  |
| `src/windows/Tools/TradingTerminal.Backtest/BacktestView.xaml` | 228 | win | TradingTerminal.Backtest | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Backtest/BacktestViewModel.cs` | 295 | win | TradingTerminal.Backtest | B I P | Y | Exports the trade list of the last run. |
| `src/windows/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml.cs` | 37 | win | TradingTerminal.Backtest | B I P | Y |  |
| `src/windows/Tools/TradingTerminal.Backtest/QuickBacktestView.xaml` | 216 | win | TradingTerminal.Backtest | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Backtest/QuickBacktestViewModel.cs` | 476 | win | TradingTerminal.Backtest | B I P | Y | How the Quick-backtest sources its replay data. |
| `src/windows/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml.cs` | 12 | win | TradingTerminal.BacktestStudio | B I P | Y | Avalonia (cross-platform) view for Backtest Studio — net9.0-leg counterpart to the WPF |
| `src/windows/Tools/TradingTerminal.BacktestStudio/AvaloniaUi/BacktestStudioAvaloniaWindow.axaml` | 63 | win | TradingTerminal.BacktestStudio | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.BacktestStudio/AxisRowViewModel.cs` | 28 | win | TradingTerminal.BacktestStudio | B I P | Y | One row in the optimization axis editor: a parameter the user can |
| `src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioServiceCollectionExtensions.cs` | 42 | win | TradingTerminal.BacktestStudio | B I P | Y | DI registration for the Backtest Studio. Seeds the kernel registry from the |
| `src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml.cs` | 151 | win | TradingTerminal.BacktestStudio | B I P | Y | Code-behind for the Studio. Pure view concern: it listens for the VM's |
| `src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioView.xaml` | 1175 | win | TradingTerminal.BacktestStudio | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioViewModel.cs` | 837 | win | TradingTerminal.BacktestStudio | B I P | Y | Exports the round-trip trades of the last single run. |
| `src/windows/Tools/TradingTerminal.BacktestStudio/DataSourceKind.cs` | 14 | win | TradingTerminal.BacktestStudio | B I P | Y | Where the Studio pulls market data from for a run. |
| `src/windows/Tools/TradingTerminal.BacktestStudio/LegacyKernelDescriptors.cs` | 32 | win | TradingTerminal.BacktestStudio | B I P | Y | Bridges the 12 legacy engine strategies (the catalog) into |
| `src/windows/Tools/TradingTerminal.BacktestStudio/ParamRowViewModel.cs` | 24 | win | TradingTerminal.BacktestStudio | B I P | Y | One editable row in the parameter panel, generated from a kernel's |
| `src/windows/Tools/TradingTerminal.BacktestStudio/ParquetMarketDataFeed.cs` | 36 | win | TradingTerminal.BacktestStudio | B I P | Y | A feed that replays a recorded parquet tick file through the new |
| `src/windows/Tools/TradingTerminal.BacktestStudio/TrialRowViewModel.cs` | 21 | win | TradingTerminal.BacktestStudio | B I P | Y | A flattened optimization trial for the results grid — the parameter dictionary |
| `src/windows/Tools/TradingTerminal.BacktestStudio/WalkForwardRowViewModel.cs` | 25 | win | TradingTerminal.BacktestStudio | B I P | Y | A walk-forward fold flattened for the results grid: the in-sample-chosen parameters and |
| `src/windows/Tools/TradingTerminal.Correlation/AvaloniaUi/LiveCorrelationAvaloniaWindow.axaml.cs` | 57 | win | TradingTerminal.Correlation | B I P | Y | Avalonia (cross-platform) view for the Live Correlation Matrix — net9.0-leg counterpart to |
| `src/windows/Tools/TradingTerminal.Correlation/AvaloniaUi/LiveCorrelationAvaloniaWindow.axaml` | 48 | win | TradingTerminal.Correlation | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixControl.cs` | 189 | win | TradingTerminal.Correlation | B I P | Y | Single source of the diverging red/grey/green heat colours (cached, frozen). |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixViewModel.cs` | 243 | win | TradingTerminal.Correlation | B I P | Y | Per-instrument fetch outcome. |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml.cs` | 32 | win | TradingTerminal.Correlation | B I P | Y | Standalone window hosting the Correlation Matrix tool. Pure view — all behaviour |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationMatrixWindow.xaml` | 284 | win | TradingTerminal.Correlation | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationPickerViewModelBase.cs` | 375 | win | TradingTerminal.Correlation | B I P | Y | Hard cap on how many rows the checklist shows at once. A |
| `src/windows/Tools/TradingTerminal.Correlation/CorrelationServiceCollectionExtensions.cs` | 19 | win | TradingTerminal.Correlation | B I P | Y | DI registration for the Correlation Matrix tools (historical + live). Transient so |
| `src/windows/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixViewModel.cs` | 246 | win | TradingTerminal.Correlation | B I P | Y | Changing the cadence live just re-paces the running sampler; the rolling window |
| `src/windows/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml.cs` | 32 | win | TradingTerminal.Correlation | B I P | Y | Standalone window hosting the Live Correlation Matrix tool. Pure view — all |
| `src/windows/Tools/TradingTerminal.Correlation/LiveCorrelationMatrixWindow.xaml` | 276 | win | TradingTerminal.Correlation | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Recording/RecorderEntry.cs` | 112 | win | TradingTerminal.Recording | B I P | Y | Live subscriptions: the ingest pumps (which do the persisting) plus the hub |
| `src/windows/Tools/TradingTerminal.Recording/RecorderPanelView.xaml.cs` | 11 | win | TradingTerminal.Recording | B I P | Y |  |
| `src/windows/Tools/TradingTerminal.Recording/RecorderPanelView.xaml` | 521 | win | TradingTerminal.Recording | B I P | N | XAML |
| `src/windows/Tools/TradingTerminal.Recording/RecorderPanelViewModel.cs` | 149 | win | TradingTerminal.Recording | B I P | Y | The recording service the whole panel binds to. |
| `src/windows/Tools/TradingTerminal.Recording/RecorderWatchlistStore.cs` | 105 | win | TradingTerminal.Recording | B I P | Y | The whole persisted recorder state — what to record and the upload |
| `src/windows/Tools/TradingTerminal.Recording/RecordingServiceCollectionExtensions.cs` | 21 | win | TradingTerminal.Recording | B I P | Y | DI registration for the live market-data recorder. |
| `src/windows/Tools/TradingTerminal.Recording/TickRecordingService.cs` | 393 | win | TradingTerminal.Recording | B I P | Y | How often auto-upload asks the archiver to ship whatever is pending. The |
