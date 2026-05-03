# TradingTerminal — Architecture Plan

## Goal

A modular trading terminal that:

- Hosts strategies as plug-ins inside a single dockable WPF shell.
- Streams market data from Interactive Brokers (TWS / IB Gateway).
- Adds new strategies with one DI-registration line, no shell edits.
- Ships v1 with a single `Example Strategy` charting NVDA on a 3-minute timeframe.

## Core principles

1. **Strict MVVM** — view-models hold all logic; code-behind only for view-only concerns.
2. **Strategies are plug-ins** — discovered via DI, never `new`'d by the shell.
3. **IB lives behind a repository** — every consumer talks to `IMarketDataRepository`; nothing else sees `EClientSocket`/`EWrapper`.
4. **Threading is a concern of one layer** — IB callbacks are marshalled to the UI dispatcher inside the repository. View-models stay single-threaded from their perspective.
5. **Async streaming via `IAsyncEnumerable<Bar>`** — natural cancellation, easy to consume, easy to fake in tests.

## Solution layout

```
TradingTerminal.sln
├── src/
│   ├── TradingTerminal.App                 (WPF entry, DI bootstrap, MainWindow, shell)
│   ├── TradingTerminal.Core                (Domain models + interfaces only — zero deps on UI/IB)
│   ├── TradingTerminal.Infrastructure      (IbClient, MarketDataRepository, ConnectionManager)
│   ├── TradingTerminal.UI                  (ViewModelBase, dark theme dictionaries, shared controls)
│   └── TradingTerminal.Strategies.Example  (ExampleStrategy + view + view-model)
└── tests/
    └── TradingTerminal.Tests               (xUnit + FluentAssertions + NSubstitute)
```

Project reference graph (acyclic):

```
App        → Infrastructure, UI, Strategies.Example, Core
Strategies → UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

## Key interfaces (signatures)

### Strategies

```csharp
public interface ITradingStrategy
{
    string Id { get; }              // e.g. "example.nvda.3m"
    string DisplayName { get; }     // e.g. "Example Strategy"
    string Description { get; }
}

public interface IStrategyFactory
{
    IReadOnlyList<ITradingStrategy> All { get; }
    StrategyHost Create(string strategyId);   // resolves view + vm pair via DI
}

public sealed record StrategyHost(string StrategyId, string DisplayName,
                                  UserControl View, ViewModelBase ViewModel);
```

A strategy's registration extension lives in its own assembly:

```csharp
public static IServiceCollection AddExampleStrategy(this IServiceCollection s) { ... }
```

The shell never references concrete strategy types.

### Market data

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
}
```

`IIbClient` is the internal abstraction over the TWS API. `RealIbClient` (compiled when `IBApi.dll` is present in `lib/`) wraps the official `EClientSocket`/`EWrapper`. `FakeIbClient` is the default — synthesizes plausible bars so the app builds and runs without the IB binary.

```csharp
internal interface IIbClient : IDisposable
{
    IObservable<ConnectionState> ConnectionState { get; }
    Task ConnectAsync(string host, int port, int clientId, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(Contract c, BarSize size, TimeSpan duration, CancellationToken ct);
    IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract c, BarSize size, CancellationToken ct);
}
```

### Cross-pane events

```csharp
public interface IEventBus
{
    IDisposable Subscribe<T>(Action<T> handler);
    void Publish<T>(T evt);
}
```

### Connection management

```csharp
internal sealed class ConnectionManager
{
    // Owns the reconnect loop with exponential backoff (1s → 30s cap).
    // Surfaces ConnectionState observable.
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

## Domain models

```csharp
public sealed record Bar(DateTime TimestampUtc, double Open, double High,
                         double Low, double Close, long Volume);

public sealed record Contract(string Symbol, string SecType, string Exchange,
                              string Currency, string PrimaryExchange);

public enum BarSize { OneMinute, ThreeMinutes, FiveMinutes, FifteenMinutes,
                      OneHour, OneDay }

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Failed }
```

## Threading model

| Source             | Thread                  | Marshalling                              |
|--------------------|-------------------------|------------------------------------------|
| `EWrapper` callbacks | IB reader thread       | Repository channels → `Dispatcher.InvokeAsync` |
| Reconnect loop     | Background `Task`       | State pushed via `BehaviorSubject`       |
| ViewModel updates  | UI thread               | All `ObservableProperty` writes here     |
| Tests              | Caller's thread         | `FakeIbClient` runs synchronously        |

## Shell layout (AvalonDock)

```
+---------------------------------------------------+
|  Title bar (MetroWindow chrome, dark)             |
|  [Disconnect banner — only when not Connected]    |
+----------------+----------------------------------+
| Strategies     |  Document pane                   |
|  - Example     |   [Example: NVDA 3m] [...]       |
|                |                                  |
|                |  (chart, parameters)             |
+----------------+----------------------------------+
|  Logs (collapsible, in-memory Serilog sink)       |
+---------------------------------------------------+
|  Status: ● Connected  | NVDA 12:34:56 | tabs: 1   |
+---------------------------------------------------+
```

## Phase plan

| # | Output                                                              |
|---|---------------------------------------------------------------------|
| 1 | This plan + repo skeleton, `.gitignore`, `.editorconfig`            |
| 2 | All csproj, DI bootstrap, MahApps + AvalonDock shell, dark theme    |
| 3 | `IbClient` (real + fake), `MarketDataRepository`, `ConnectionManager` |
| 4 | `ExampleStrategy` view + vm + ScottPlot binding                     |
| 5 | ListBox-to-tab wiring, disconnect banner, logs pane, status bar     |
| 6 | Four required xUnit tests                                           |
| 7 | README, XML doc comments, formatting pass                           |

## Assumptions

- **Target framework: `net9.0-windows`.** Only the .NET 9 SDK is installed on this machine; .NET 8 isn't available. WPF works identically on net9.
- **IB binary delivery.** The TWS API is not on nuget.org. The build expects `lib/IBApi.dll` (copied from the user's TWS API install — usually `C:\TWS API\source\CSharpClient\IBApi\bin\Release\netstandard2.0\IBApi.dll`). When absent, the `FakeIbClient` is registered by default so the app still builds, runs, and demos the chart with synthetic bars. Setting `InteractiveBrokers:UseRealClient = true` in `appsettings.json` switches to the real client.
- **Default IB port 7497** (paper trading socket on TWS). Switch to 7496 for live or 4002 for IB Gateway as needed.
