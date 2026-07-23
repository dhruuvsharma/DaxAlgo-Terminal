# symbols — shared Windows API router

Generated signatures live in `symbols/*.md`; this master only routes high-value seams. Search the
smallest shard before opening implementation:

```powershell
rg -n "SubscribeDepthAsync|IMarketDataIngest" .claude/context/symbols/
```

Core and Infrastructure are split by top-level folder (for example `Core-MarketData.md` and
`Infrastructure-Ib.md`). Generated entries show declaration lines and file anchors; multi-line
signatures show their first line, and source-generated properties may not appear.

## Hot seams

| Concern | Start in | Contract to preserve |
|---|---|---|
| Broker abstraction | `Core-MarketData.md` → `IBrokerClient` | SDK-neutral API; cancellation unsubscribes; implementations read their own options |
| Canonical identity/events | `Core-Domain.md`, `Core-MarketData.md` | `InstrumentId`; event/ingest time, source, sequence, approximation provenance |
| Ingest and fanout | `Core-MarketData.md`, `MarketData.md` | VMs use `IMarketDataHub`/`IMarketDataIngest`; tick-primary, bounded, non-blocking persistence |
| Broker selection/login | `Core-Brokers.md`, `Login.md`, `Infrastructure-Root.md` | credentialed forms and brokers remain paired; Basic stays keyless |
| Strategy plugin SDK | `Core-Strategies.md`, `DaxAlgo.Sdk.md`, `Infrastructure-Plugins.md` | external `IStrategyPlugin`; no shell ProjectReference to strategy implementations |
| Backtest contracts | `Core-Backtest.md`, `Core-Backtesting.md`, `Backtest.Engine.md` | deterministic clock/router lifecycle; no live execution path |
| Shared UI lifecycle | `UI.Core.md` | bounded feeds, coalesced render, deterministic subscription/timer disposal |
| Research reproduction | `Core-Research.md`, `Infrastructure-Research.md` | third-party code remains out-of-process and sandboxed |
| AI analyst seam | `Core-AiAnalyst.md`, `Ai.md` | Null/HTTP parity; sidecar over loopback HTTP/JSON |
| Edition policy | `Core-Configuration.md` | Basic, Intermediate, and consuming-overlay composition remain explicit |

Windows first-party strategy implementations are external runtime plugins. A
`symbols/Strategies.*.md` shard is stale by definition.

## Project routing

| Question | Shard(s) |
|---|---|
| Per-broker clients | `Infrastructure-<Broker>.md` |
| Store/archive backends | `MarketData.md`, `Infrastructure-Notifications.md` |
| Fills, fees, risk, engine orchestration | `Infrastructure-Backtest.md`, `Backtest.Engine.md` |
| Quant, ML, regime, research | `Core-Quant.md`, `Core-Ml.md`, `Core-Regime.md`, `Core-Research.md` |
| WPF themes/shared controls | `UI.md`, `UI.Core.md` |
| Charts and market tools | `Charts.md`, `OrderBook.md`, `VolumeFootprint.md`, `Heatmap.md` |
| Tools | `AdvancedMarketRegime.md`, `Backtest.md`, `BacktestStudio.md`, `Correlation.md`, `Recording.md` |
| Shell composition/login | `App.Basic.md`, `App.Intermediate.md`, `Login.md` |
| Settings/authoring/composed UI | `Settings.md`, `StrategyComposer.md` |
| Code generation/authoring CLI | `DaxAlgo.Codegen.md`, `DaxAlgo.StrategyTool.md` |
| SDK and sample plugin | `DaxAlgo.Sdk.md`, `DaxAlgo.SamplePlugin.md` |
| Windows tests | matching entries in `symbols/` plus `index/Tests.md` |
