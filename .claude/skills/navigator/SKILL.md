---
name: navigator
description: Codebase map for DaxAlgo Terminal — which project owns what, where key seams/files live, and which skill to load next. Load this FIRST for orientation ("where is X?", "which project owns Y?", "where do I add Z?") instead of grepping blind. Cheap one-shot map; saves repeated searches.
---

# Navigator — where things live

Layer graph (acyclic): `Core ← MarketData ← Infrastructure ← {Login, Ai, Ai.*, Strategies.*, <tool projects>} ← App`. `UI ← Core`; App also → UI/MarketData. Never make `Core` or `MarketData` depend upward. **Tool projects and AI tool projects sit at the same layer as `Strategies.*` — App is the only thing that references them, and no tool references another tool.**

## Project ownership

| Project | Owns |
|---|---|
| `TradingTerminal.Core` | Domain types + **all interfaces/options**. `Brokers/` (`IBrokerClient`, `IBrokerSelector`, `BrokerKind`, `IBrokerLoginForm[Factory]`), `MarketData/` (`InstrumentId`, `Quote`/`TradePrint`/`OhlcvBar`, `IMarketDataHub|Store|Ingest|Repository|Registry`, `Indicators`, `Microstructure`), `Backtest/` (`IBacktestStrategy`, `IOrderRouter`, `IFeeModel`, `IRiskManager`, `TransactionCostAnalysis`), `Notifications/` (`INotificationPublisher|Transport|Enricher`, `ISignalGate`), `AiAnalyst/` (`IAiAnalystClient`), `Strategies/` (`IStrategyFactory`, `Parameters/`, `Authoring/`), `Regime/`, `Session/`, `Configuration/`. |
| `TradingTerminal.MarketData` | Pipeline **impls** (below Infrastructure, Core-only): `MarketDataHub`, `MarketDataIngestService`, `MarketDataRepository`, `InstrumentDiscoveryService`, `MarketDataPipelineServiceCollectionExtensions`, `Store/` (Sqlite + Npgsql + schema + registry), `Archive/` (Telegram offloader), `Threading/IUiDispatcher`. |
| `TradingTerminal.Infrastructure` | Broker clients `Ib/ Ninja/ CTrader/ Alpaca/` (behind `IBrokerClient`), `Backtest/` (engine + `Strategies/` engine-side impls + `BacktestStrategyCatalog` + `WalkForwardGridBuilders`), `Notifications/` (dispatcher + Telegram/Discord/Ollama transports + `NotificationsOptions`/`AiAnalystOptions`), `Regime/`, `Threading/WpfDispatcher`, `DependencyInjection.cs`. |
| `TradingTerminal.UI` | `ViewModelBase`, `Themes/`, `LiveSignalStrategyViewModelBase`, `LiveStrategyHostServices` (DI bundle), `Logging/InMemoryLogSink` (**the universal Activity Log**), `Strategies/` (parameter-editor controls), converters. |
| `TradingTerminal.Login` | `LoginWindow`, `LoginViewModel`, `CredentialStore`, `Forms/` (Ib/Ninja/CTrader/Alpaca login forms), `BrokerLoginFormFactory`, `AddLogin()`. Namespaces stay `TradingTerminal.App.Login[.Forms]`. |
| `TradingTerminal.Ai` | **Shared seam only**: `Analyst/` (`Http`/`Null`AiAnalystClient, `AiAnalystServiceCollectionExtensions` → `AddAiAnalyst()`, `AiAnalystEnricher`). UI tools moved out (see below). |
| `TradingTerminal.Ai.<Name>` | One AI tool per project + its `Add<Name>()` extension: `Ai.MarketAnalyst` (dock pane), `Ai.FactorResearch`, `Ai.MlFeatures`, `Ai.BacktestAnalysis`. |
| `TradingTerminal.Strategies.<Name>` | Per-strategy live window + VM + `ITradingStrategy` descriptor + `Add<Name>Strategy()`. 10 of them (incl. OrderFlowPressureMap — a multi-ticker monitor strategy; strategy-shaped things NEVER become tool projects). |
| `TradingTerminal.<Name>` (tool projects) | One tool window per project + its `Add…Surface` extension: `Charts`, `OrderBook`, `VolumeFootprint`, `Correlation`, `MarketRegime`, `InstrumentRegime`, `MarkovRegime`, `Backtest` (tab), `Recording`. Each refs Core/UI/Infrastructure; opened from App via `IServiceProvider`. |
| `TradingTerminal.App` | `App.xaml.cs` (composition root + `OnStartup`), `Composition/AppDependencyInjection.cs` (`AddStrategyPlugins`/`AddShell`/`AddSettingsSurface`/`AddArchiveSurface` — tool surfaces now live in their own projects), `Shell/` (MainWindow, factories, DockTab), `MainWindow.xaml(.cs)` (top menu incl. **Charts** menu), `MainWindowViewModel`, `Notifications/`, `Archive/`, broker-meter. Thin shell — tools moved out. |
| `TradingTerminal.Backtest.Cli` | `daxalgo-backtest` headless CLI (`Program.cs`: run/synth/sweep/walkforward/mc/tca/features). |

## "Where do I…" quick answers

- **Add a strategy** → new `Strategies.<Name>` project + engine impl in `Infrastructure/Backtest/Strategies/` + catalog entry + CLI arm + `AddStrategyPlugins()` line + `App.csproj` ref + `.sln`. Skill: `add-strategy` (cube/surface: `regime-cube-strategy`).
- **Add a broker** → `Infrastructure/<Broker>/` impl + login form in `Login/Forms/` (`AddLogin`). Skills: `add-broker`, `broker-gotchas`.
- **Add a notifier** → `Infrastructure/Notifications/<X>/` + `AddNotifications`. Skill: `add-notifier`.
- **Touch the data pipeline / store / archive** → `TradingTerminal.MarketData`. Skills: `market-data-pipeline`, `archive-offloader`.
- **Touch AI/ML** → the seam is in `TradingTerminal.Ai`; the UI tools are in `TradingTerminal.Ai.<Name>`. Skill: `ai-analyst`.
- **Add/edit a tool window** → its own `TradingTerminal.<Name>` project (flat under `src/`, grouped in the `.sln` Charts/Tools/AI folders). New tool = new project + its `Add…Surface` extension + `App.csproj` ref + `App.xaml.cs` call + `.sln` (`dotnet sln add --solution-folder <Folder> --include-references false`).
- **Touch the backtest engine / CLI** → `Infrastructure/Backtest/`. Skill: `backtest-engine`.
- **Log from a strategy/tab** → `_services.ActivityLog.Append(source, level, msg)` (live VMs) or `InMemoryLogSink`; the MainWindow `ACTIVITY LOG` pane shows it. Never add a per-window log panel.
- **Register a service** → `Composition/AppDependencyInjection.cs` (App-side) or the owning project's `Add*` extension, called from `App.xaml.cs`.

## DI entry points (called from `App.xaml.cs`)

Infrastructure/pipeline: `AddInfrastructure` · `AddMarketDataPipeline` · `AddMarketDataArchive` · `AddMarketRegime` (provider/refresh, Infrastructure) · `AddNotifications` · `AddStrategyPlugins` · `AddLogin` · `AddShell` · `AddSettingsSurface` · `AddArchiveSurface`.
Tool surfaces (each defined in its own project): `AddBacktestSurface` · `AddRecordingSurface` · `AddCorrelationSurface` · `AddChartsSurface` · `AddOrderBookSurface` · `AddFootprintSurface` · `AddMarketRegimeSurface` · `AddInstrumentRegimeSurface` · `AddMarkovRegimeSurface`.
AI (shared seam + per-tool): `AddAiAnalyst` (seam, in `TradingTerminal.Ai`) · `AddMarketAnalyst` · `AddFactorResearch` · `AddMlFeatures` · `AddBacktestAnalysis`.

## Build / test

`dotnet build` · `dotnet test` · `dotnet run --project src/TradingTerminal.App`. A Stop hook auto-surfaces build errors. The 10 live strategies: ApexScalper, CumulativeDelta, ImbalanceHeatFront, IndexKScoreSurface, OrderFlowCube, OrderFlowPressureMap, OrderFlowSurfaceSpike, OrderFlowToxicity (vpin), OrnsteinUhlenbeck, VolatilityTargeted (+ buyAndHold/meanReversion/donchian engine demos). Removed strategies — don't reference them: Bollinger, Rsi, Macd, Microprice, Twap, AnomalyDetector, ConnorsRsi2, EodMomentum, GapFade, LondonOpenBreakout, MaCrossover, TrendFilter, AvellanedaStoikov, BookPressure, IcebergDetection, LiquiditySweep, OnlineRegressionAlpha, PullbackContinuation, ThinBookFilter.
