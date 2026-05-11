# DaxAlgo Terminal — Claude Working Guide

A modular **multi-broker** WPF trading terminal. WPF + .NET 9. Three broker backends behind one `IBrokerClient` seam: Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), and cTrader (Spotware Open API 2.0 over TLS+protobuf). This file is the playbook for Claude in this repo. Read it once, work from it — don't re-derive conventions every session.

## Stack snapshot

- **Target framework**: `net9.0-windows` (no .NET 8 SDK on the box).
- **MVVM**: `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators.
- **Shell**: MahApps Metro chrome + AvalonDock VS2013 Dark theme.
- **Charts**: ScottPlot 5.
- **DI**: `Microsoft.Extensions.DependencyInjection`.
- **Logging**: Serilog with custom in-memory sink for the Logs pane.
- **Notifications**: `INotificationPublisher` (Core) → bounded `Channel<>` + hosted `NotificationDispatcher` (Infra) → `INotificationTransport`s (currently Telegram). Settings tab persists to `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`, hot-reloaded via `IOptionsMonitor`.
- **Tests**: xUnit + FluentAssertions + NSubstitute. WPF-touching tests use `[WpfFact]` (`Xunit.StaFact`).
- **Brokers** — one IBrokerClient implementation per:
  - **IB** — `RealIbClient` compiled when `CSharpAPI.dll` resolves (lib/, MSBuild prop, or `C:\TWS API\…`). Else `FakeIbClient`.
  - **NinjaTrader** — `RealNinjaClient` compiled when `NTDirect.dll` resolves (lib/, MSBuild prop, or `%USERPROFILE%\Documents\NinjaTrader 8\bin64\`). Else `FakeNinjaClient`.
  - **cTrader** — `RealCTraderClient` always wired (`cTrader.OpenAPI.Net` package always restores). `FakeCTraderClient` exists for tests/offline; not on the default DI graph.

## Solution graph (do not break this)

```
App        → Infrastructure, UI, Strategies.*, Core
Strategies → UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

`Core` has zero deps on UI / WPF / IB / NT / cTrader. New domain types go there. **New broker code goes in `Infrastructure/<Broker>/` behind `IBrokerClient` — view-models never see `EClientSocket`/`NTDirect`/`OpenClient`.** New strategies are their own `TradingTerminal.Strategies.<Name>` project.

## Architectural rules

1. **Strict MVVM.** No business logic in `.xaml.cs`. View-models inherit `ViewModelBase`. Use `[ObservableProperty]` and `[RelayCommand]`.
2. **Strategies are plug-ins.** Discovered via DI via `StrategyFactoryRegistration`. Adding a strategy = new project + one `services.AddMyStrategy()` line in `App.xaml.cs`. The shell never references strategy concretes.
3. **Brokers are plug-ins too.** Adding a broker = new `IBrokerClient` implementation + DI block. The `MarketDataRepository`, `ConnectionManager`, view-models, and strategies stay untouched.
4. **Threading is a one-layer concern.** Broker callbacks marshal to the UI dispatcher inside `MarketDataRepository`. View-models stay single-threaded from their POV. Don't sprinkle `Dispatcher.Invoke` in view-models.
5. **Async streaming via `IAsyncEnumerable<Bar>` / `IAsyncEnumerable<Tick>`.** Use `[EnumeratorCancellation]` on the `CancellationToken` parameter. Cancellation is the natural unsubscribe path.
6. **Connection state is observable.** `IObservable<ConnectionState>` flows from `ConnectionManager` → repository → view-models. Don't poll.
7. **Reconnect with exponential backoff.** Already wired (1s → 30s cap). When adding new broker calls, assume the connection can drop mid-call; surface failures as state transitions, don't crash.
8. **`IBrokerClient.ConnectAsync` takes no params.** Each implementation reads its own configured options (`IOptions<XxxOptions>`). The login form pushes user-supplied values into those options before flipping the selector.

## Per-broker gotchas (the stuff that bites)

### Interactive Brokers
- **TWS handles its own 2FA.** The API socket has no separate 2FA step. Don't add 2FA prompts to the login screen.
- **ClientId must be unique** across all simultaneously connected clients (Excel, Bookmap, another instance). Error 326 = collision.
- **Default ports**: 7497 TWS Paper, 7496 TWS Live, 4002 Gateway Paper, 4001 Gateway Live.
- **Real client compiles only when `HAS_IBAPI` is defined** (set by `Infrastructure.csproj` when `CSharpAPI.dll` resolves). Code that touches `IBApi.*` types must be inside `#if HAS_IBAPI`.
- **The TWS API is not on NuGet.** Don't suggest `dotnet add package IBApi`.
- **EWrapper callbacks are on the IB reader thread.** Always marshal before touching observable collections.

### NinjaTrader 8
- **NT must be running first.** `NTDirect.Connected(0)` returns 0 only when NT 8 is up with **Tools → Options → AT Interface → AT Interface enabled**.
- **Real client compiles only when `HAS_NTAPI` is defined** (set when `NTDirect.dll` resolves). Code that touches `NTDirect` must be inside `#if HAS_NTAPI`.
- **NTDirect doesn't expose historical bars** — `RequestHistoricalBarsAsync` synthesizes them with a warning log. Don't promise real history without a NinjaScript bridge add-on.
- **NTDirect doesn't expose L1 sizes** via the simple `Bid`/`Ask` calls — `Tick.BidSize`/`AskSize` are 0 in NT mode.
- **Polling, not callbacks.** Tick stream polls `Bid`/`Ask` at 200 ms; bar stream aggregates polled `LastPrice`.

### cTrader
- **Always wired** — the NuGet package restores unconditionally. There's no `HAS_CTRADERAPI` gate.
- **Real client requires OAuth credentials** (clientId + clientSecret + accessToken + ctidTraderAccountId). When missing, `ConnectAsync` reports `ConnectionState.Failed` with a clear log. Don't add a synthetic fallback in the DI graph; the `Failed` state IS the affordance.
- **Access tokens expire** (~30 days). Surface refresh failures as a clear error pointing at OAuth — don't suppress them.
- **Wire prices are `ulong`** scaled by `10^Digits` per symbol. We resolve `Digits` lazily via `ProtoOASymbolByIdReq` on first subscribe.
- **Trendbars use relative encoding** (Low + DeltaOpen/DeltaHigh/DeltaClose). Reconstruct OHLC then divide by `10^Digits`.
- **Per-call correlation via `clientMsgId`** + a `Dictionary<string, TaskCompletionSource<IMessage>>`. Spot events bypass the request/response router; subscribers filter the OpenClient's stream by `SymbolId` directly.

## Backtesting

- **Engine seam.** `IBacktestStrategy` (in `Core/Backtest/`) is the engine-facing contract — `OnStart`/`OnTick`/`OnOrderEvent`/`OnEnd`. Strategies place orders through `IOrderRouter` (in `Core/Trading/`), never through `IBrokerClient` directly. Live: `LiveOrderRouter` delegates to the active broker. Backtest: `BacktestOrderRouter` pushes into a `SimulatedOrderBook` evaluated by `L1FillModel` on every tick.
- **Data.** Tick streams live in parquet via `ParquetTickReader`/`Writer` (`Infrastructure/Backtest/Persistence/`). Row-group buffered; epoch-microsecond timestamps. The CLI's `synth` subcommand generates a mean-reverting random walk for smoke testing.
- **Surfaces.** Two: `TradingTerminal.Backtest.Cli` (headless, `daxalgo-backtest.exe`), and the **Tools → Backtest** tab in the WPF shell (`App/Backtest/`).
- **Adding a backtest strategy.** Implement `IBacktestStrategy`, then register in `Infrastructure/Backtest/Strategies/` (or your own assembly). Add to `BacktestStrategyCatalog.cs` (UI dropdown) and `ResolveStrategy` in CLI `Program.cs`.
- **Stats.** `StatisticsCalculator` computes Sharpe/Sortino annualised from the median equity-sample gap; max drawdown as a fraction of peak; per-trade win-rate/profit-factor/expectancy. Equity is sampled at most once per minute of simulated time.
- **OMS stubs.** `IBrokerClient.PlaceOrderAsync`/`CancelOrderAsync`/`OrderEvents` exist but the three real clients throw `NotSupportedException`. That's the seam OMS will fill — don't add a `LiveOrderRouter → broker.PlaceOrderAsync` path until OMS lands.

## Build & run

```powershell
dotnet build
dotnet test
dotnet run --project src/TradingTerminal.App

# Backtest CLI (after build):
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe synth --output bt-data.parquet --ticks 10000
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe run --strategy meanReversion --symbol TEST --data bt-data.parquet
```

Build prints, when applicable:
- `IB CSharpAPI resolved from: <path>` — IB real client compiled in.
- `NTDirect resolved from: <path>` — NT real client compiled in.

cTrader is always compiled in.

The repo defaults to `InteractiveBrokers:UseRealClient: true` since `C:\TWS API\` is the standard install path. NinjaTrader defaults to `false` (must be explicitly enabled). cTrader requires credentials at the form.

## Reasoning patterns (how to approach work in this repo)

| Task shape | Pattern |
|---|---|
| **Bug fix** | Read the failing code first. No plan needed for one- or two-file fixes. Reproduce mentally, then patch. |
| **New strategy** | Follow the 5-step recipe in README §"Adding a new strategy". Don't edit shell files beyond the one DI line. |
| **New broker** | Follow README §"Adding a new broker". Implement `IBrokerClient` in `Infrastructure/<Broker>/`. Both `Real*` and `Fake*`. One DI block. One login tile + form. Don't touch consumers. |
| **New broker call** (e.g. orders) | Add to `IBrokerClient` first (broker-neutral signature). Implement in all three real clients + all three fakes. Then expose via `IMarketDataRepository`. View-models last. |
| **XAML/binding fix** | Check `DataContext` flow. Confirm `[ObservableProperty]` generates the expected `PropertyName`. AvalonDock layouts can swallow binding errors silently — check Output window. |
| **Refactor across layers** | Plan first. Respect the project graph; never make `Core` depend on anything. |
| **Anything async + broker** | Cancellation tokens flow end-to-end. Test with the relevant `Fake*Client` first. |

## Delegation (when to spawn a subagent)

Subagents have model overrides — the right one cuts cost without losing quality.

| Subagent | Model | Use for |
|---|---|---|
| `wpf-explorer` | Haiku | "Where is X defined?", "which files reference Y?", finding XAML resources, view-model lookups. Cheap reads. |
| `xaml-fixer` | Sonnet | Targeted XAML/binding/style fixes. Theming work. AvalonDock layout tweaks. |
| `ib-api-expert` | Opus | TWS API wiring, EWrapper callbacks, threading bugs in IB code, contract/historical-data subtleties. (Currently IB-specialised — broaden if NT/cTrader work gets equally hairy.) |
| `dotnet-reviewer` | Sonnet | Pre-commit review of staged changes. Catches MVVM violations, layer-graph breaks, missing `ConfigureAwait(false)` in library code, nullable issues. |

**Default**: do simple work in the main thread. Spawn an Explore subagent when a search will need 3+ rounds. Spawn `ib-api-expert` when touching `Infrastructure/Ib/` non-trivially.

## Code style preferences

- File-scoped namespaces.
- `internal` by default; `public` only at module boundaries.
- No comments unless the *why* is non-obvious. Identifiers should explain *what*.
- No defensive null-checks on internal calls — trust the type system. Validate at boundaries (config load, broker callbacks, user input).
- No `ConfigureAwait(false)` in WPF view-model code (we *want* to resume on the UI thread). Use it in pure-library code if any is added.
- Records for value types (`Bar`, `Tick`, `Contract`). Sealed by default.
- Prefer `IReadOnlyList<T>` over `List<T>` in public signatures.

## What NOT to do

- Don't add 2FA logic to the login window (TWS handles its own; NT relies on the running instance; cTrader uses OAuth).
- Don't rename `net9.0-windows` to `net8.0-windows` (no .NET 8 SDK).
- Don't put `IBApi` / `NTDirect` / cTrader proto types in `Core` or `UI`.
- Don't `new` strategies from the shell — go through `IStrategyFactory`.
- Don't `new` broker clients from the shell — go through `IBrokerSelector`.
- Don't add NuGet sources for IB — the DLL resolution is already wired.
- Don't add `--no-verify` to git commits to bypass hooks.
- Don't make `Core` depend on anything.

## When unsure

The architecture document (`docs/architecture.md`) has the full design rationale and key interface signatures. Check it before adding new abstractions — chances are there's already a slot to plug into.
