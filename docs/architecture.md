# DaxAlgo Terminal — Architecture

## Goal

A modular trading terminal that:

- Hosts strategies as plug-ins inside a single dockable WPF shell.
- Talks to **multiple brokers** through a single seam — picked by the user at login, swapped without any consumer noticing.
- Adds new strategies with one DI-registration line, no shell edits.
- Adds new brokers with one new `IBrokerClient` implementation + one DI block, no consumer changes.

The shipped brokers exercise four very different transports on purpose, to prove the abstraction holds:

| Broker | Transport | Process model | History | Live ticks | L2 depth | Auth |
|---|---|---|---|---|---|---|
| Interactive Brokers | TCP socket → `EClientSocket`/`EWrapper` (170+ callbacks) | TWS desktop app, local | Real (`reqHistoricalData`) | Real (`reqMktData` L1) | `reqMktDepth` exists, not yet wired — throws | TWS handles 2FA itself |
| NinjaTrader 8 | P/Invoke into `NTDirect.dll` (ANSI C ABI) | NT 8 desktop app, local, in-process bridge | None — synthesized | Real, polled at 200 ms | Not exposed by `NTDirect` — needs a NinjaScript bridge, out of scope | NT instance is the auth boundary |
| cTrader | TLS + protobuf to Spotware cloud (`cTrader.OpenAPI.Net`) | None — pure-cloud, no local desktop required | Real (`ProtoOAGetTrendbarsReq`) | Real, push (`ProtoOASpotEvent`) | Real, push (`ProtoOASubscribeDepthQuotesReq` / `ProtoOADepthEvent`, incremental new/deleted quotes → local book reconstruction → `DepthSnapshot`s) | OAuth 2.0 (clientId + secret + access token) |
| Alpaca | REST (history) + WebSocket (live ticks) via `Alpaca.Markets` | None — pure-cloud | Real (`HistoricalBarsRequest` / `HistoricalCryptoBarsRequest`) | Real, push (`IAlpacaDataStreamingClient.GetQuoteSubscription`) | Not exposed by Alpaca — `SubscribeDepthAsync` throws | Static API key id + secret (no OAuth) |

If the abstraction holds for all four, it holds for whatever comes next (Tradovate, Rithmic, dxTrade, …).

## Core principles

1. **Strict MVVM** — view-models hold all logic; code-behind is for view-only concerns.
2. **Strategies are plug-ins** — discovered via DI, never `new`'d by the shell.
3. **Brokers are plug-ins too** — every broker call goes through `IBrokerClient`. View-models never see `EClientSocket`, `NTDirect`, or `OpenClient`.
4. **One layer owns threading** — broker callbacks are marshalled to the UI dispatcher inside the repository. Everything above stays single-threaded from its perspective.
5. **Async streaming via `IAsyncEnumerable<T>`** — natural cancellation, easy to consume, easy to fake in tests.
6. **Reactive connection state** — `IObservable<ConnectionState>` flows from the connection manager up. Nothing polls.

## Solution layout

```
TradingTerminal.sln
├── src/
│   ├── TradingTerminal.App                       WPF entry, DI bootstrap, MainWindow, LoginWindow
│   │   └── Notifications/                        Settings tab VM/View + per-user notifications.json writer
│   ├── TradingTerminal.Backtest.Cli              Headless backtest runner — run / synth / sweep subcommands
│   ├── TradingTerminal.Core                      Domain models + interfaces — zero deps on UI/brokers
│   │   ├── Backtest/                             IBacktestStrategy, BacktestConfig/Result/Stats (+Calmar/Omega/Ulcer/Recovery/DownsideDev/MaxConsecLosses)
│   │   ├── Brokers/                              BrokerKind, IBrokerSelector, BrokerConnectionMode
│   │   ├── Configuration/                        InteractiveBrokers/NinjaTrader/CTrader/Alpaca options, MarketDataStoreOptions, MarketRegimeOptions
│   │   ├── Domain/                               Bar, Tick, Contract, BarSize, ConnectionState, InstrumentId/Instrument/InstrumentAlias, AssetClass,
│   │   │                                          canonical Quote/TradePrint/OhlcvBar records (event+ingest time, source, seq, approx flag)
│   │   ├── Events/                               IEventBus, EventBus
│   │   ├── MarketData/                           IBrokerClient, IMarketDataRepository,
│   │   │                                          IMarketDataHub / IMarketDataStore / IMarketDataIngest / IInstrumentRegistry (canonical pipeline),
│   │   │                                          Microstructure helpers, Indicators (SMA/EMA/RSI/ATR/stdev)
│   │   ├── Notifications/                        StrategyNotification, INotificationPublisher, INotificationTransport, ISignalGate, NotificationKind
│   │   ├── Regime/                               IMarketRegimeProvider, MarketRegimeCalculator (pure), Snapshot/State/Category/Inputs records
│   │   ├── Risk/                                 IRiskManager, RiskManager (per-symbol pos cap + daily PnL cap), RiskOptions
│   │   ├── Session/                              SessionContext
│   │   ├── Strategies/                           ITradingStrategy, IStrategyFactory, StrategyHost
│   │   ├── Time/                                 IClock
│   │   └── Trading/                              IOrderRouter, OrderRequest/Result/Event (+ LiquidityFlag), IFeeModel (zero/maker-taker/bps)
│   ├── TradingTerminal.Infrastructure
│   │   ├── Backtest/                             Engine — SimulatedClock, L1FillModel, SimulatedOrderBook (tags Maker/Taker),
│   │   │                                          BacktestOrderRouter (risk-aware), BacktestSession, TradeLedger (fee-aware),
│   │   │                                          StatisticsCalculator, Persistence/ParquetTick{Reader,Writer},
│   │   │                                          Strategies/ — 15+ implementations (HFT, FX, index baselines)
│   │   ├── Brokers/                              BrokerSelector
│   │   ├── Ib/                                   RealIbClient (#if HAS_IBAPI), FakeIbClient, ConnectionManager
│   │   ├── NinjaTrader/                          RealNinjaClient (#if HAS_NTAPI), FakeNinjaClient
│   │   ├── CTrader/                              RealCTraderClient, FakeCTraderClient
│   │   ├── Alpaca/                               RealAlpacaClient (REST history + WS ticks for stocks + crypto; no synthetic fallback)
│   │   ├── MarketData/                           MarketDataRepository,
│   │   │                                          MarketDataHub (Rx fanout), MarketDataIngestService (ref-counted, normalizes per-broker timestamps),
│   │   │                                          Store/ — MarketDataStoreBase + Sqlite{MarketDataStore,InstrumentPersistence,Schema}
│   │   │                                          and Npgsql{MarketDataStore,InstrumentPersistence} + TimescaleSchema (Postgres/TimescaleDB)
│   │   ├── Notifications/                        Dispatcher (channel + hosted worker), Telegram + Discord transports, AllowAllSignalGate (default)
│   │   ├── Regime/                               MarketRegimeService, RegimeRefreshLoop (IHostedService), RegimeSignalGate (ISignalGate impl),
│   │   │                                          Yahoo/FRED/CNN/AAII clients
│   │   ├── Time/                                 SystemClock
│   │   ├── Trading/                              LiveOrderRouter (delegates to active IBrokerClient)
│   │   └── Threading/                            IUiDispatcher, WpfDispatcher
│   ├── TradingTerminal.UI                        ViewModelBase, dark theme, in-memory log sink
│   ├── TradingTerminal.Strategies.Rsi            RSI Overbought/Oversold strategy
│   └── TradingTerminal.Strategies.CumulativeDelta  Cumulative Delta Scalper (sniper-mode, 5-confirmation gate)
└── tests/
    └── TradingTerminal.Tests                     xUnit + FluentAssertions + NSubstitute
```

Project reference graph (acyclic):

```
App        → Infrastructure, UI, Strategies.*, Core
Strategies → Infrastructure, UI, Core   (live VM wraps an engine-side IBacktestStrategy)
Infra      → Core
UI         → Core
Core       → (nothing)
```

`Core` knows nothing about WPF, MahApps, AvalonDock, IB, NT, or cTrader. New abstractions go into `Core`; new SDK calls go into `Infrastructure`. The per-strategy projects under `TradingTerminal.Strategies.<Name>/` are thin live-UI wrappers — they hold the `MetroWindow`, view-model, and `ITradingStrategy` descriptor, but the actual signal logic (which they instantiate inside `BuildStrategy(contract)`) lives in `Infrastructure/Backtest/Strategies/`. That split keeps the same `IBacktestStrategy` reusable from both the backtest engine and the live signal mode.

## Key interfaces

### Broker abstraction

```csharp
public enum BrokerKind { InteractiveBrokers, NinjaTrader, CTrader, Alpaca }

public interface IBrokerClient : IAsyncDisposable
{
    BrokerKind Kind { get; }
    IObservable<ConnectionState> ConnectionState { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration,
        CancellationToken ct = default);

    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default);

    IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, CancellationToken ct = default);

    IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default);
}

public interface IBrokerSelector
{
    BrokerKind ActiveKind { get; }
    IBrokerClient Active { get; }
    BrokerConnectionMode ActiveMode { get; }
    event EventHandler? ActiveChanged;
    void SetActive(BrokerKind kind);
}

public sealed record BrokerConnectionMode(
    BrokerKind Broker, bool IsLive, string DisplayName, string Description);
```

`ConnectAsync` takes no host/port/clientId — each implementation reads its own configured options (`IOptions<InteractiveBrokersOptions>` / `IOptions<NinjaTraderOptions>` / `IOptions<CTraderOptions>` / `IOptions<AlpacaOptions>`). This keeps the interface broker-agnostic.

### Repository

```csharp
public interface IMarketDataRepository
{
    IObservable<ConnectionState> ConnectionState { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration,
        CancellationToken ct = default);

    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default);

    IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, CancellationToken ct = default);
}
```

`MarketDataRepository` resolves `IBrokerSelector.Active` for every call and marshals every yielded bar/tick onto the UI dispatcher before handing it back. View-models stay single-threaded.

### Canonical market-data pipeline

`IMarketDataRepository` is the broker-facing seam — one selector lookup per call, raw `Bar`/`Tick` records, no persistence. Sitting in front of it is a **broker-neutral pipeline** that resolves canonical identity, normalizes provenance, fans out live, and writes through to a local store. Strategies and view-models can keep talking to the repository, or move to the pipeline for warm-up history + a uniform stream regardless of which broker is connected.

```
broker socket          ┌── canonical record ──► IMarketDataHub (Rx, in-memory fanout) ──► strategies / panels
   │                   │                                                                       (subscribe by InstrumentId)
   ▼                   │
IBrokerClient ──► IMarketDataIngest ── normalize ──► canonical Quote / TradePrint / OhlcvBar
                       │                                                                       
                       └── batched async writes ──► IMarketDataStore ──► SQLite | Postgres/Timescale
                                                                         (warm-up, replay, research)
```

Four Core seams under `Core/MarketData/` + `Core/Domain/`:

```csharp
public readonly record struct InstrumentId(int Value);            // surrogate, broker-neutral pk

public sealed record Instrument(InstrumentId Id, string CanonicalSymbol,
    AssetClass AssetClass, string Exchange, string Currency,
    double TickSize, double Multiplier);

public sealed record InstrumentAlias(InstrumentId InstrumentId,
    BrokerKind Broker, string BrokerSymbol, string? BrokerNativeId);

public interface IInstrumentRegistry
{
    Instrument? Get(InstrumentId id);
    InstrumentId? Resolve(BrokerKind broker, string brokerSymbol);
    InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker);  // auto-registers on first sight
    string? ToBrokerSymbol(InstrumentId id, BrokerKind broker);
    void RegisterAlias(InstrumentAlias alias);
    IReadOnlyList<Instrument> All();
}

public interface IMarketDataHub
{
    IObservable<Quote>          Quotes(InstrumentId id);
    IObservable<TradePrint>     Trades(InstrumentId id);
    IObservable<OhlcvBar>       Bars(InstrumentId id, BarSize size);
    IObservable<DepthSnapshot>  Depth(InstrumentId id);
    void PublishQuote(Quote q);         // ingest only
    void PublishTrade(TradePrint t);    // ingest only
    void PublishBar(OhlcvBar b);        // ingest only
    void PublishDepth(InstrumentId id, DepthSnapshot s);
}

public interface IMarketDataStore
{
    void EnqueueQuote(Quote q);         // non-blocking; channel → batch writer
    void EnqueueTrade(TradePrint t);
    void EnqueueBar(OhlcvBar b);        // upserts in-progress bars by (id, size, openTime)
    Task FlushAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId id, BarSize size, int count, CancellationToken ct = default);
    IAsyncEnumerable<Quote>      ReadQuotesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, ...);
    IAsyncEnumerable<TradePrint> ReadTradesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, ...);
}

public interface IMarketDataIngest
{
    InstrumentId Resolve(Contract contract);
    IDisposable  Subscribe(Contract contract);            // quotes + depth, ref-counted
    IDisposable  SubscribeBars(Contract contract, BarSize size);
}
```

**Canonical records.** Unlike the legacy quote-only `Tick`, every record carries both `EventTimeUtc` and `IngestTimeUtc`, the originating `BrokerKind Source`, a per-instrument monotonic `Sequence`, and an `EventTimeApproximate` flag. Brokers that only report arrival time (IB, NT, cTrader stamp ticks with `DateTime.UtcNow` in their callbacks) get their event time copied from ingest time and the flag set — so consumers always know whether the timestamp is authoritative. `TradePrint` fills the gap quote-only feeds left (Alpaca publishes trades; the other three currently don't, so `TradePrint` ingest is unsourced until a broker trade-stream is wired).

**Two storage backends, one base.** `MarketDataStoreBase` owns the channel + batch + flush loop; concrete backends only spell out their schema and `INSERT` statements:

- **`SqliteMarketDataStore`** — embedded, WAL mode, `Microsoft.Data.Sqlite`. Timestamps stored as epoch-microseconds. Default DB path `%LOCALAPPDATA%\DaxAlgoTerminal\marketdata.db`. Zero-config.
- **`NpgsqlMarketDataStore`** — PostgreSQL with the free Apache-2 TimescaleDB extension over `Npgsql`. `timestamptz` columns; quotes/trades/bars are hypertables. Connection string defaults match the `docker-compose.yml` service.

`IInstrumentRegistry` likewise has both `SqliteInstrumentPersistence` and `NpgsqlInstrumentPersistence` behind one in-memory cached `InstrumentRegistry`. Backend is picked once in DI from `MarketDataStoreOptions.Provider` (`Sqlite` | `Postgres`); when `Postgres` is configured but the network probe in the factory fails (Docker not running), the app **transparently falls back to SQLite** so it always starts — logged at warning level.

**Ingest is ref-counted per instrument.** N strategies subscribing to the same canonical id share one broker stream; the underlying `IBrokerClient` subscription stops when the last handle is disposed. Depth snapshots are live-only (not persisted — bandwidth would dwarf everything else).

The pipeline is registered as one line in `App.xaml.cs` via `services.AddMarketDataPipeline(configuration)`. The legacy `IMarketDataRepository` path is still wired and unchanged — the pipeline is additive.

### Connection management

```csharp
public sealed class ConnectionManager : IAsyncDisposable
{
    public ConnectionManager(IBrokerSelector selector, ILogger<ConnectionManager> logger);
    public IObservable<ConnectionState> ConnectionState { get; }
    public Task StartAsync(CancellationToken ct);
    public Task StopAsync(CancellationToken ct);
    public Task RequestReconnectAsync(CancellationToken ct);
    public void ConfigureBackoff(TimeSpan initial, TimeSpan max);
}
```

The manager subscribes to `IBrokerSelector.ActiveChanged` and re-wires its underlying `IBrokerClient` when the user switches brokers — the reconnect loop continues seamlessly. Backoff is exponential (1 s → 30 s cap by default).

### Strategies

```csharp
public interface ITradingStrategy
{
    string Id { get; }              // e.g. "rsi.overbought-oversold"
    string DisplayName { get; }
    string Description { get; }
}

public interface IStrategyFactory
{
    IReadOnlyList<ITradingStrategy> All { get; }
    StrategyHost Create(string strategyId);
}

public sealed record StrategyHost(string StrategyId, string DisplayName,
                                  UserControl View, ViewModelBase ViewModel);
```

Each strategy assembly registers itself via `services.AddXxxStrategy()`. The shell never references concrete strategy types.

### Cross-pane events

```csharp
public interface IEventBus
{
    IDisposable Subscribe<T>(Action<T> handler);
    void Publish<T>(T evt);
}
```

Used for things like "strategy opened", "connection state changed" — anything where the originator and the listeners shouldn't know about each other.

### Notifications

```csharp
public interface INotificationPublisher
{
    ValueTask PublishAsync(StrategyNotification notification, CancellationToken ct = default);
}

public interface INotificationTransport
{
    string Name { get; }
    bool IsEnabled { get; }
    Task SendAsync(StrategyNotification notification, CancellationToken ct);
}

public sealed record StrategyNotification(
    NotificationKind Kind, string StrategyId, string StrategyName,
    string Symbol, string? Direction, string Message, DateTime TimestampUtc);
```

Strategies inject `INotificationPublisher` and call `PublishAsync` whenever a signal fires (e.g. `RsiStrategyViewModel.EvaluateSignal`, `CumulativeDeltaViewModel.EvaluateSignal`). Calls return immediately — under the hood, the publisher writes to a bounded `Channel<StrategyNotification>` (drop-oldest on overflow). A single hosted background worker (`NotificationDispatcher : IHostedService`) drains the channel and fans each message out to every transport that reports `IsEnabled`. Transport failures are caught, logged, and isolated — one channel can't block another.

Settings (bot token, chat ID, enable flag) live in `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`, layered onto host configuration with `reloadOnChange: true`. Transports read `IOptionsMonitor<NotificationsOptions>.CurrentValue` on each send, so edits from the Settings tab take effect without a restart.

An `ISignalGate` seam sits between the publisher and the transport fanout — it can veto a notification just before send-out, with a short human-readable reason for the log. The default implementation (`AllowAllSignalGate`) is a no-op; the market-regime feature swaps in `RegimeSignalGate` to suppress `Signal`-kind notifications while the composite is below the risk-off threshold (see below). Decoupling the gate keeps the dispatcher unaware of any specific feature.

Two transports ship: **Telegram** (Bot API, JSON over HTTPS) and **Discord** (channel webhook, JSON over HTTPS). Each has its own options block (`NotificationsOptions.Telegram` / `NotificationsOptions.Discord`), its own enable flag, and its own `IncludeIdleSignals` knob — they're fully independent.

Adding another transport (Slack, email, SMS) = one class implementing `INotificationTransport` in `Infrastructure/Notifications/<Channel>/`, plus options/persistence/UI plumbing modelled on the Telegram or Discord scaffolding. The dispatcher discovers transports via `IEnumerable<INotificationTransport>`.

#### AI Market Analyst

A second enricher path (alongside the local-LLM Ollama commentary) runs a multi-agent **AI Market Analyst** against every signal. The C# side is broker- and provider-agnostic:

```csharp
public interface IAiAnalystClient
{
    bool IsAvailable { get; }
    Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default);
}

public sealed record AnalystReport(
    AiAnalystDecision Decision, string ForecastHorizon, double RiskRewardRatio,
    double Confidence, string Justification,
    IndicatorReport Indicator, PatternReport Pattern, TrendReport Trend,
    string PatternChartPngBase64, string TrendChartPngBase64, long ElapsedMs);
```

Two implementations: `NullAiAnalystClient` (default — always returns "unavailable") and `HttpAiAnalystClient` (calls the Python sidecar over `http://127.0.0.1:<port>/analyst/run`). A `DispatchingAiAnalystClient` reads `IOptionsMonitor<NotificationsOptions>.CurrentValue` on every call so the Settings toggle hot-swaps Null ↔ Http without a restart. The actual reasoning lives in a Python sidecar (`tools/python-ml/daxalgo-ml.exe`) — a four-agent LangGraph (indicator → pattern → trend → decision) with TA-Lib indicators, mplfinance candle rendering, and vision-LLM pattern matching against a 16-pattern classical catalog. See [`polyglot.md`](polyglot.md) for the subprocess + HTTP/JSON seam contract and the rationale for keeping Python out-of-process.

API keys are stored DPAPI-encrypted under `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` (same `DataProtectionScope.CurrentUser` pattern as Alpaca/cTrader) — never in `appsettings.json`. The terminal degrades gracefully: with no sidecar running the WPF pane shows "AI Analyst unavailable" and the enricher silently passes notifications through unchanged. Ollama and AI Analyst are independent enrichers; both run in registration order, neither replaces the other.

### Market regime composite

A broker-independent "is the market risk-on or risk-off right now" score, ported from the upstream `worldmonitor` project's Fear/Greed model and rewritten behind `Core` types. The composite is a weighted blend of ten sub-signals (volatility, positioning, trend, breadth, momentum, credit, liquidity, macro, sentiment, cross-asset) mapped to a 0–100 score with five bands (Extreme Fear / Fear / Neutral / Greed / Extreme Greed).

```csharp
public interface IMarketRegimeProvider
{
    MarketRegimeSnapshot Current { get; }                          // never null; Empty before first refresh
    IObservable<MarketRegimeSnapshot> Updates { get; }             // hot, replays the latest to new subscribers
    Task<MarketRegimeSnapshot> RefreshAsync(CancellationToken ct = default);
}

public sealed record MarketRegimeSnapshot(
    double CompositeScore, RegimeState State, double? PreviousScore,
    IReadOnlyList<RegimeCategoryScore> Categories,
    RegimeHeaderMetrics Header, DateTime GeneratedAtUtc, bool Unavailable);
```

The actual math lives in `Core/Regime/MarketRegimeCalculator` — a pure function that takes a `RegimeInputs` bag and emits a snapshot. Inputs come from four free public endpoints in `Infrastructure/Regime/`:

- **`YahooChartClient`** — Yahoo Finance v8 chart endpoint, always on, no key. Drives volatility (VIX), trend (price vs 200dma), breadth, momentum, cross-asset.
- **`FredClient`** — St. Louis Fed FRED REST API. Needs a free key (`MarketRegime:FredApiKey`); enables the credit / liquidity / macro categories plus the 10y / HY-spread / Fed-funds header metrics. Missing key degrades those categories to a neutral 50 rather than failing the composite.
- **`CnnFearGreedClient`** — CNN Money's Fear & Greed dataviz endpoint, no key. Scraped, can break; toggleable via `UseCnnFearGreed`.
- **`AaiiSentimentClient`** — AAII weekly bull/bear sentiment survey. Scraped HTML; lowest reliability, always non-blocking.

`MarketRegimeService` orchestrates the pulls (graceful per-source degradation), `RegimeRefreshLoop : IHostedService` recomputes on a configurable cadence (`MarketRegime:RefreshMinutes`, 5-minute floor to stay polite to Yahoo), and `RegimeSignalGate` (implementing `ISignalGate`, see Notifications above) suppresses outbound `Signal` notifications when `GateSignalsWhenRiskOff` is on and the composite is at or below `RiskOffThreshold`. The signal still appears in the strategy's own dashboard — only the outbound alert is dropped. Regime band changes optionally fire their own `NotificationKind.RegimeChange` alert.

The dockable **Tools → Market regime** panel binds to `Updates`, paints the gauge + category breakdown + header metrics, and shows "unavailable" until the first refresh lands.

### Backtest engine

```csharp
public interface IBacktestStrategy
{
    Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct);
    Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct);
}

public interface IOrderRouter
{
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    IObservable<OrderEvent> OrderEvents { get; }
}
```

Strategies are router-only — they never see `IBrokerClient`. `LiveOrderRouter` (Infra) delegates to the active broker; `BacktestOrderRouter` (Infra) routes into a `SimulatedOrderBook` evaluated by `L1FillModel` on every tick, after consulting an optional `IRiskManager`.

Twenty-plus textbook strategies ship — HFT/microstructure (Avellaneda-Stoikov, microprice, OU, TWAP), FX baselines (Bollinger, MA cross, RSI(2), London open, MACD), S&P 500 baselines (200-SMA trend filter, vol targeting, gap fade, EOD momentum, pullback continuation), and L2 / depth-of-market (book pressure / cumulative imbalance, liquidity sweep, iceberg detection, VPIN-style toxicity, thin-book filter). The L2-themed strategies compute on L1 sizes today since the backtest parquet schema is L1-only; each has a docstring marking the exact line that switches to a `DepthSnapshot` computation when L2 ticks land. They share two helper modules in `Core/MarketData/`: `Indicators` (streaming SMA, EMA, Wilder RSI, ATR, rolling Bessel-corrected stdev) and `Microstructure` (L1: microprice, queue imbalance, half-spread; L2: `CumulativeImbalance`, `WeightedMidPrice`, `SideDepth`, `EstimatedSlippage`, `LargestLevelGap`).

### Risk + fees

```csharp
public interface IRiskManager
{
    (bool Allowed, string? RejectReason) Evaluate(OrderRequest request);
    void RecordFill(string symbol, OrderEvent fillEvent);
}

public interface IFeeModel
{
    double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity);
}

public enum LiquidityFlag { Taker, Maker }
```

`BacktestOrderRouter` runs `IRiskManager.Evaluate` before every submission and surfaces rejections as `OrderEvent` with `State=Rejected` on the strategy's existing event stream — strategies need no special path. `OrderEvent.Liquidity` is set by the simulated order book based on `OrderType` (Limit ⇒ Maker; Market/Stop ⇒ Taker); `TradeLedger` charges `IFeeModel.Fee` against cash per fill and surfaces totals on `BacktestResult.TotalFees`.

## Domain models

```csharp
public sealed record Bar(DateTime TimestampUtc, double Open, double High,
                         double Low, double Close, long Volume);

public sealed record Tick(DateTime TimestampUtc, double Bid, double Ask,
                          long BidSize, long AskSize);

public sealed record DepthLevel(double Price, long Size);

public sealed record DepthSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<DepthLevel> Bids,  // sorted descending
    IReadOnlyList<DepthLevel> Asks); // sorted ascending

public sealed record Contract(string Symbol, string SecType, string Exchange,
                              string Currency, string PrimaryExchange);

public enum BarSize { OneMinute, ThreeMinutes, FiveMinutes, FifteenMinutes,
                      OneHour, OneDay }

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }
```

Records, sealed by default, all in `Core`. No broker-specific fields leak in.

## Threading model

| Source | Thread | Marshalling |
|---|---|---|
| `EWrapper` callbacks (IB) | IB reader thread | Per-request channels → repository → `Dispatcher.InvokeAsync` |
| `NTDirect` polling loop (NT) | Background `Task` | Same channel + dispatch path |
| `OpenClient` `IObservable<IMessage>` (cTrader) | Library worker | Same channel + dispatch path |
| Reconnect loop | Background `Task` | State pushed via `BehaviorSubject` |
| `IMarketDataStore` writes | Background `Channel<>` consumer per backend | Ingest hot path never blocks on disk; batched flush on size or interval |
| `RegimeRefreshLoop` polls | `IHostedService` worker | Snapshots pushed via `IObservable<MarketRegimeSnapshot>` |
| View-model updates | UI thread | All `[ObservableProperty]` writes happen here |
| Tests | Caller's thread | Synthetic `IBrokerClient` substitutes run synchronously; `ImmediateDispatcher` skips the marshal |

## Per-broker integration notes

### Interactive Brokers — `RealIbClient` (gated by `#if HAS_IBAPI`)

- Wraps `IBApi.EClientSocket`/`EWrapper`. We inherit `DefaultEWrapper` to get free no-ops on the 170+ callbacks we don't care about.
- `eConnect` is async-by-callback: `nextValidId` resolves the connect TCS.
- An `EReader` thread pumps incoming messages; `processMsgs` dispatches them to our overridden callbacks.
- Per-request state lives in `Dictionary<int, …>` keyed by `reqId`, guarded by a single `lock`.
- L1 quote stream synthesizes `Tick` records from `tickPrice`/`tickSize` callbacks. Field IDs 1/2/0/3 (live) and 66/67/75/76 (delayed) both honored — works whether or not the user has a real-time market-data subscription.

### NinjaTrader 8 — `RealNinjaClient` (gated by `#if HAS_NTAPI`)

- Pure P/Invoke into `NTDirect.dll`. Functions are ANSI C exports: `Connected`, `SubscribeMarketData`, `Bid`, `Ask`, `LastPrice`, `Volume`, `Command(...)` for orders.
- NT must be running first — `NTDirect.Connected(0)` returns 0 only when NinjaTrader is up with the AT Interface enabled.
- No callback API. Tick stream polls `Bid`/`Ask` at 200 ms; bar stream aggregates polled `LastPrice` over the bar window.
- No historical-data export. `RequestHistoricalBarsAsync` synthesizes a series anchored on the current `LastPrice` so charts have a baseline.
- L1 sizes aren't exposed via `Bid`/`Ask` — `Tick.BidSize` / `Tick.AskSize` come back as 0.

### cTrader — `RealCTraderClient` (always wired)

- Uses the `cTrader.OpenAPI.Net` package's `OpenClient` — TLS socket, protobuf framing, internal heartbeat.
- Connect → `ProtoOAApplicationAuthReq` → `ProtoOAAccountAuthReq` → one-shot `ProtoOASymbolsListReq` to populate the symbol catalog.
- Per-call request/response correlation via a per-call `clientMsgId` + a `Dictionary<string, TaskCompletionSource<IMessage>>`. Incoming `ProtoMessage` envelopes are inspected, the inner payload decoded by `PayloadType`, and routed to the matching pending TCS.
- Spot events (`ProtoOASpotEvent`) bypass the request/response router — each `SubscribeTicksAsync` filter their stream by `SymbolId` directly.
- Wire prices are `ulong` scaled by `10^Digits` per symbol. We learn `Digits` lazily via `ProtoOASymbolByIdReq` on first subscribe.
- Trendbars are encoded relative to `Low` (`Low + DeltaOpen/DeltaHigh/DeltaClose`). Reconstructed and rescaled in `RequestHistoricalBarsAsync`.
- Access tokens expire — `ProtoOAErrorRes` surfaces this clearly; the user re-runs the OAuth refresh and pastes the new token.
- **L2 depth** is wired via `ProtoOASubscribeDepthQuotesReq` + `ProtoOADepthEvent`. Each event carries `NewQuotes` (add/replace, keyed by quote id) and `DeletedQuotes` (remove by id). Quotes are one-sided (either `Bid` or `Ask` set, not both); we maintain `Dictionary<ulong, DepthLevel>` per side and emit a consistent top-N `DepthSnapshot` after each event. `ProtoOADepthEvent.SymbolId` is `uint64` (not `int64` like `ProtoOASpotEvent`) — the filter casts before comparing.

### Alpaca — `RealAlpacaClient` (always wired)

- Uses the `Alpaca.Markets` SDK against `Environments.Paper` (`paper-api.alpaca.markets`) or `Environments.Live` (`api.alpaca.markets`). The same `SecretKey(ApiKey, ApiSecret)` works for trading, stock data, crypto data, and the streaming endpoints — one credential pair, no OAuth dance.
- One client instance multiplexes asset classes by `Contract.SecType`: `STK` / `STOCK` / `EQUITY` route through `IAlpacaDataClient` + `IAlpacaDataStreamingClient`; `CRYPTO` / `CRYPTOCURRENCY` route through `IAlpacaCryptoDataClient` + `IAlpacaCryptoStreamingClient`. Options route is reserved for when the SDK's options surface stabilises — it currently throws.
- Both streaming clients are connected and authenticated **eagerly** inside `ConnectAsync` (not lazily on first subscribe), so the first tick subscription doesn't pay the auth round-trip.
- Live ticks come in as `IQuote` events on a per-symbol `IAlpacaDataSubscription`; the handler maps them into `Tick(TimestampUtc, Bid, Ask, BidSize, AskSize)` and writes to an unbounded `Channel<Tick>`. Unsubscribe on stream cancellation unhooks the handler and calls `UnsubscribeAsync` on the SDK side.
- Live bars are aggregated from the tick stream (same approach as cTrader) so the bar cadence (`BarSize`) stays configurable rather than being pinned to the SDK's native minute bars.
- Stock data feed is configurable: `iex` (free) or `sip` (paid consolidated). Crypto and options have their own consolidated feeds and ignore the setting.
- **No L2 depth.** Alpaca's WebSocket API only emits NBBO-style L1 quotes; `SubscribeDepthAsync` throws `NotSupportedException`. Strategies that need depth must route through IB (`reqMktDepth`, pending plumbing) or cTrader.
- **No `Fake*Client`.** Unlike IB / NT / cTrader, Alpaca has no synthetic fallback — credentials are mandatory to use that tile. The other three brokers fall back to a synthetic random-walk when their real DLL or OAuth isn't configured.

## Login flow

```
                   ┌──────────────────────────────────────────────┐
                   │              LoginWindow shows               │
                   │ ┌─────────┐ ┌─────────┐ ┌────────┐ ┌────────┐│
                   │ │   IB    │ │   NT    │ │cTrader │ │ Alpaca ││
                   │ └────┬────┘ └────┬────┘ └────┬───┘ └────┬───┘│
                   └──────┼───────────┼───────────┼──────────┼────┘
                          ▼           ▼           ▼          ▼
                  one form per broker, conditional on SelectedBroker
                                       │
                                       ▼
                       user clicks "Sign in"
                                       │
                                       ▼
        ┌──────────────────────────────────────────────────────┐
        │ LoginViewModel.ConnectAsync:                         │
        │  1. Push form values into the active broker's        │
        │     IOptions<XxxOptions>                             │
        │  2. brokerSelector.SetActive(SelectedBroker)         │
        │     → ConnectionManager re-wires                     │
        │  3. repository.ConnectAsync(ct)                      │
        │  4. Wait on ConnectionState observable for Connected │
        │     or Failed (15 s timeout)                         │
        └──────────────────────────────────────────────────────┘
                                       │
                                       ▼
                          ┌────────────────────────┐
                          │ MainWindow takes over. │
                          │ ConnectionManager owns │
                          │ reconnect loop.        │
                          └────────────────────────┘
```

The user's selection persists in `connection.json` (under `%LOCALAPPDATA%\DaxAlgoTerminal\`) so the next launch reopens to the same broker form. IB password, cTrader OAuth secrets, and the Alpaca API secret are DPAPI-encrypted under `DataProtectionScope.CurrentUser`.

## Shell layout (AvalonDock)

```
+-------------------------------------------------------+
|  MetroWindow chrome (dark)                            |
|  [Disconnect banner — only when not Connected]        |
+----------------+--------------------------------------+
| Strategies     |  Document pane                       |
|  - RSI         |   [RSI: NVDA 3m] [Cumulative Delta]  |
|  - CumDelta    |                                      |
|                |  (chart, parameters, controls)       |
+----------------+--------------------------------------+
|  Logs (pinned, in-memory Serilog sink)                |
+-------------------------------------------------------+
|  ● Connected | dhruv · cTrader #1234 | Live cTrader   |
+-------------------------------------------------------+
```

The status-bar mode badge reflects the active broker's `BrokerConnectionMode` — green when live, yellow when synthetic, plus the broker's own display name. It updates live when the user switches brokers.

## Testing strategy

Tests live in `tests/TradingTerminal.Tests` and use xUnit + FluentAssertions + NSubstitute:

- **`StrategyFactory` tests** — registers a strategy, resolves it by id, sets the `DataContext`. Throws on unknown ids. Uses a `[WpfFact]` STA test for the WPF-touching part.
- **`MarketDataRepository.SubscribeBarsAsync`** — propagates the underlying `IBrokerClient`'s "not connected" error. Uses a `SingleClientSelector` test double so the test exercises the full pipeline (selector → manager → repository → consumer) without a real broker.
- **`ConnectionManager`** — reconnects after the underlying client drops, observable transitions through `Connecting → Connected → Reconnecting`. Uses a `FlakyClient` that fails its first connection.
- **Backtest engine** — buy-then-sell across a synthetic tick window produces one trade with the expected price/PnL; parquet tick round-trip preserves byte-for-byte content; `StatisticsCalculator` returns expected ratios on a known curve.
- **Fee model** — Maker/Taker per-unit and Bps-on-notional charges; ending cash drops by exactly the fee amount; `BacktestResult.TotalFees` matches.
- **Risk manager** — per-symbol cap rejects accumulating positions; daily loss cap rejects after threshold and resets at UTC midnight; duplicate fills are idempotent.
- **Microstructure** — microprice leans toward the thinner side, equals the mid when sizes are equal, falls back to mid when sizes are zero; queue imbalance is bounded and signed.
- **Indicators** — SMA, EMA recursion, Wilder-RSI saturation in both directions, ATR mean-of-|Δ|, rolling stdev with Bessel correction.
- **Canonical pipeline** — `InstrumentRegistry` resolves the same canonical id for repeated `Contract` lookups (idempotent), `MarketDataHub` fans one publish out to multiple `InstrumentId` subscribers, `MarketDataIngestService` normalizes per-broker timestamps (sets `EventTimeApproximate` for brokers that only report arrival time). Store backends covered by a round-trip test per backend: `SqliteMarketDataStoreTests` always runs; `NpgsqlMarketDataStoreTests` self-skips when Docker / Postgres isn't reachable.
- **Market regime** — `MarketRegimeCalculator` produces the expected composite + band on a hand-built `RegimeInputs` (extreme-fear / extreme-greed corner cases, neutral fallback when sources are missing).

The synthetic `Fake*Client` per broker isn't unit-tested directly but doubles as a smoke test for the full DI graph.

## Code-style preferences

- File-scoped namespaces.
- `internal` by default; `public` only at module boundaries.
- No comments unless the *why* is non-obvious. Identifiers should explain the *what*.
- No defensive null-checks on internal calls — trust the type system. Validate at boundaries (config load, broker callbacks, user input).
- No `ConfigureAwait(false)` in WPF view-model code (we *want* to resume on the UI thread). Use it in pure-library code if any is added.
- Records for value types (`Bar`, `Tick`, `Contract`). Sealed by default.
- Prefer `IReadOnlyList<T>` over `List<T>` in public signatures.

## Assumptions

- **`net9.0-windows`.** Only the .NET 9 SDK is installed on the dev box. WPF works identically.
- **Synthetic data is always usable.** `Fake*Client` per broker means the build runs even with zero broker setup.
- **Per-broker SDK delivery.**
  - IB: `CSharpAPI.dll` sideloaded; auto-discovered from `lib/`, an MSBuild prop, or the standard `C:\TWS API\…` path. Build prints the resolved path.
  - NT: `NTDirect.dll` sideloaded; auto-discovered from `lib/`, an MSBuild prop, or `%USERPROFILE%\Documents\NinjaTrader 8\bin64\`. Copied next to the assembly so P/Invoke finds it.
  - cTrader: `cTrader.OpenAPI.Net` from NuGet — always restored, no gate.
  - Alpaca: `Alpaca.Markets` from NuGet — always restored, no gate.
- **Single account per broker, read-mostly v1.** The live strategies (RSI, CumDelta) are read-only (chart + signals). Order plumbing is in place via broker-specific `Command(...)` / `ProtoOANewOrderReq` paths but `IBrokerClient.PlaceOrderAsync` still throws `NotSupportedException` on the three real clients — that's the OMS seam. Once OMS lands, the existing `IRiskManager` will wrap `LiveOrderRouter` so live and backtest share the same accounting.
- **Backtest is router-first.** The backtest engine and the live engine share `IOrderRouter`; backtest strategies execute against `BacktestOrderRouter` + `SimulatedOrderBook`, live strategies will execute against `LiveOrderRouter` + the active broker. Same `IRiskManager` / `IFeeModel` slots both sides of the seam.

## Polyglot tools (forthcoming)

Some workloads outgrow the managed C# engine — a 50M-tick backtest is awkward in .NET, and the Python ML ecosystem (sklearn, pandas, torch) is not realistically replaceable. The plan is to keep the WPF build hermetic by isolating other languages behind a **subprocess + file/JSON seam**, not via P/Invoke or embedded interpreters:

- **`tick-backtester`** — C++20 one-shot subprocess for high-throughput tick replay. C# writes a JSON config + Parquet input, reads back JSON stats + Parquet equity/trades.
- **`daxalgo-ml`** — Python 3.11 FastAPI service (loopback only, ephemeral port, no auth) spawned lazily on first AI-tab use. Bulk data crosses as Parquet paths; models persist to `~/.daxalgo/models/`.

See [`polyglot.md`](polyglot.md) for the full design, the layout under `tools/`, the migration order, and why each language earns its keep.
