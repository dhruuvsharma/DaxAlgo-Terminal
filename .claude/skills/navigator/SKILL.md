---
name: navigator
description: Codebase map for DaxAlgo Terminal — which project owns what, where key seams/files live, and which skill to load next. Load this FIRST for orientation ("where is X?", "which project owns Y?", "where do I add Z?") instead of grepping blind. Cheap one-shot map; saves repeated searches.
---

# Navigator — where things live

Layer graph (acyclic): `Core ← MarketData ← Infrastructure ← {Login, Ai, Strategies.*} ← App`. `UI ← Core`; App also → UI/MarketData. Never make `Core` or `MarketData` depend upward.

## Project ownership

| Project | Owns |
|---|---|
| `TradingTerminal.Core` | Domain types + **all interfaces/options**. `Brokers/` (`IBrokerClient`, `IBrokerSelector`, `BrokerKind`, `IBrokerLoginForm[Factory]`), `MarketData/` (`InstrumentId`, `Quote`/`TradePrint`/`OhlcvBar`, `IMarketDataHub|Store|Ingest|Repository|Registry`, `Indicators`, `Microstructure`), `Backtest/` (`IBacktestStrategy`, `IOrderRouter`, `IFeeModel`, `IRiskManager`, `TransactionCostAnalysis`), `Notifications/` (`INotificationPublisher|Transport|Enricher`, `ISignalGate`), `AiAnalyst/` (`IAiAnalystClient`), `Strategies/` (`IStrategyFactory`, `Parameters/`, `Authoring/`), `Regime/`, `Session/`, `Configuration/`. |
| `TradingTerminal.MarketData` | Pipeline **impls** (below Infrastructure, Core-only): `MarketDataHub`, `MarketDataIngestService`, `MarketDataRepository`, `InstrumentDiscoveryService`, `MarketDataPipelineServiceCollectionExtensions`, `Store/` (Sqlite + Npgsql + schema + registry), `Archive/` (Telegram offloader), `Threading/IUiDispatcher`. |
| `TradingTerminal.Infrastructure` | Broker clients `Ib/ Ninja/ CTrader/ Alpaca/` (behind `IBrokerClient`), `Backtest/` (engine + `Strategies/` engine-side impls + `BacktestStrategyCatalog` + `WalkForwardGridBuilders`), `Notifications/` (dispatcher + Telegram/Discord/Ollama transports + `NotificationsOptions`/`AiAnalystOptions`), `Regime/`, `Threading/WpfDispatcher`, `DependencyInjection.cs`. |
| `TradingTerminal.UI` | `ViewModelBase`, `Themes/`, `LiveSignalStrategyViewModelBase`, `LiveStrategyHostServices` (DI bundle), `Logging/InMemoryLogSink` (**the universal Activity Log**), `Strategies/` (parameter-editor controls), converters. |
| `TradingTerminal.Login` | `LoginWindow`, `LoginViewModel`, `CredentialStore`, `Forms/` (Ib/Ninja/CTrader/Alpaca login forms), `BrokerLoginFormFactory`, `AddLogin()`. Namespaces stay `TradingTerminal.App.Login[.Forms]`. |
| `TradingTerminal.Ai` | `Analyst/` (`Http`/`Null`AiAnalystClient, `AiAnalystServiceCollectionExtensions`, `AiAnalystEnricher`), `AnalystUi/` (AI Market Analyst dock), `Tools/` (ML features, backtest analysis), `Research/` (factor research), `AddAi()`. |
| `TradingTerminal.Strategies.<Name>` | Per-strategy live window + VM + `ITradingStrategy` descriptor + `Add<Name>Strategy()`. 16 of them. |
| `TradingTerminal.App` | `App.xaml.cs` (composition root + `OnStartup`), `Composition/AppDependencyInjection.cs` (the `AddXxx` manifest), `Shell/` (MainWindow, factories, DockTab), `MainWindow.xaml(.cs)`, `MainWindowViewModel`, broker-meter, archive UI bridge. |
| `TradingTerminal.Backtest.Cli` | `daxalgo-backtest` headless CLI (`Program.cs`: run/synth/sweep/walkforward/mc/tca/features). |

## "Where do I…" quick answers

- **Add a strategy** → new `Strategies.<Name>` project + engine impl in `Infrastructure/Backtest/Strategies/` + catalog entry + CLI arm + `AddStrategyPlugins()` line + `App.csproj` ref + `.sln`. Skill: `add-strategy` (cube/surface: `regime-cube-strategy`).
- **Add a broker** → `Infrastructure/<Broker>/` impl + login form in `Login/Forms/` (`AddLogin`). Skills: `add-broker`, `broker-gotchas`.
- **Add a notifier** → `Infrastructure/Notifications/<X>/` + `AddNotifications`. Skill: `add-notifier`.
- **Touch the data pipeline / store / archive** → `TradingTerminal.MarketData`. Skills: `market-data-pipeline`, `archive-offloader`.
- **Touch AI/ML** → `TradingTerminal.Ai`. Skill: `ai-analyst`.
- **Touch the backtest engine / CLI** → `Infrastructure/Backtest/`. Skill: `backtest-engine`.
- **Log from a strategy/tab** → `_services.ActivityLog.Append(source, level, msg)` (live VMs) or `InMemoryLogSink`; the MainWindow `ACTIVITY LOG` pane shows it. Never add a per-window log panel.
- **Register a service** → `Composition/AppDependencyInjection.cs` (App-side) or the owning project's `Add*` extension, called from `App.xaml.cs`.

## DI entry points (called from `App.xaml.cs`)

`AddInfrastructure` · `AddMarketDataPipeline` · `AddMarketDataArchive` · `AddMarketRegime` · `AddNotifications` · `AddStrategyPlugins` · `AddLogin` · `AddShell` · `AddBacktestSurface` · `AddSettingsSurface` · `AddRecordingSurface` · `AddCorrelationSurface` · `AddChartsSurface` (TradingView-style WebView2 chart, `App/Charts/`) ·
`AddAi` · `AddRegimeSurface` · `AddArchiveSurface`.

## Build / test

`dotnet build` · `dotnet test` · `dotnet run --project src/TradingTerminal.App`. A Stop hook auto-surfaces build errors. Removed strategies (Bollinger, Rsi, Macd, Microprice, Twap, AnomalyDetector, ConnorsRsi2, EodMomentum, GapFade, LondonOpenBreakout, MaCrossover, TrendFilter) — don't reference them.
