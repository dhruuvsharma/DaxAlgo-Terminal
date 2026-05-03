# TradingTerminal

[![.NET 9](https://img.shields.io/badge/.NET-9.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A modular WPF trading terminal that hosts strategies as plug-ins inside a dockable shell. Streams market data from Interactive Brokers (TWS / IB Gateway). Ships v1 with one strategy — **Example Strategy** — that charts NVDA on a 3-minute timeframe.

## Highlights

- **MVVM** with `CommunityToolkit.Mvvm` (no business logic in code-behind).
- **Strategy + Factory + Repository** patterns. New strategies plug in via one DI line — no shell edits.
- **AvalonDock** layout (left: strategies, center: tabs, bottom: logs, status bar).
- **Dark theme** layered on MahApps Metro + AvalonDock VS2013 Dark.
- **ScottPlot 5** candlestick chart (auto-scrolling, last ~200 bars, configurable timeframe).
- **Auto-reconnect** with exponential backoff (1s → 30s cap), surfaced as a red banner with a Reconnect button.
- **Async-first** market data via `IAsyncEnumerable<Bar>`. IB callbacks are marshalled to the UI dispatcher inside the repository — view-models never see threading.
- **Logs pane** wired through Serilog with a custom in-memory sink.
- **xUnit + FluentAssertions + NSubstitute** tests (factory, repository, view-model, reconnect).

## Prerequisites

| Tool                          | Version                                                                   |
|-------------------------------|---------------------------------------------------------------------------|
| .NET SDK                      | 9.x (the project targets `net9.0-windows`)                                |
| Windows                       | 10 / 11                                                                   |
| Interactive Brokers TWS or IB Gateway | Installed and logged in. Paper or live, your call.                |
| TWS API ("CSharpAPI")         | Optional but required for real market data — see [Enabling real IB](#enabling-real-ib). |

## Run it

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/TradingTerminal.App -c Release
```

A login screen opens first — enter a username (display only) and your IB password (stored locally if you tick Remember; encrypted with Windows DPAPI under your user). Connection settings (Host / Port / Client ID / Account) live in the Connection settings expander; defaults match TWS Paper. Sign in attempts a real socket connection to TWS — on success the main shell opens.

> **Note**: TWS / IB Gateway authenticates separately. Make sure you're logged in there first; the API only opens a socket to the running TWS instance.

Out of the box the app uses a synthetic `FakeIbClient` so you get a live-looking chart with no IB setup. Click **Example Strategy** in the left pane to open the chart.

## Enabling real IB

The TWS API is not on nuget.org. Follow these steps to wire up real market data:

1. Install the TWS API (https://www.interactivebrokers.com/en/trading/ib-api.php) — this drops a `TWS API` folder under `C:\TWS API\`.
2. Locate `IBApi.dll`. Common path:
   `C:\TWS API\source\CSharpClient\IBApi\bin\Release\netstandard2.0\IBApi.dll` (you may need to open the `.sln` under `C:\TWS API\source\CSharpClient\` and build it once).
3. Copy `IBApi.dll` into `lib/` at the root of this repo.
4. In `appsettings.json` set:

   ```json
   "InteractiveBrokers": {
     "UseRealClient": true
   }
   ```

5. Rebuild. The build picks up the DLL via a conditional reference and defines the `HAS_IBAPI` symbol — `RealIbClient` compiles in and is selected by DI.

### Configuring TWS / IB Gateway

In TWS: **File → Global Configuration → API → Settings**:

- ✅ **Enable ActiveX and Socket Clients**
- ✅ **Read-Only API** (recommended for now — strategy v1 is read-only)
- **Socket port**: `7497` (TWS Paper) / `7496` (TWS Live) / `4002` (Gateway Paper) / `4001` (Gateway Live)
- **Trusted IPs**: add `127.0.0.1`

Then update `appsettings.json` to match your port.

## Configuration (`appsettings.json`)

| Key                                    | Default           | Notes                                                        |
|----------------------------------------|-------------------|--------------------------------------------------------------|
| `InteractiveBrokers:Host`              | `127.0.0.1`       |                                                              |
| `InteractiveBrokers:Port`              | `7497`            | TWS Paper. Use 7496 / 4001 / 4002 as applicable.             |
| `InteractiveBrokers:ClientId`          | `1`               | Must be unique across all clients connected to the same TWS. |
| `InteractiveBrokers:AccountType`       | `Paper`           | Cosmetic only.                                               |
| `InteractiveBrokers:UseRealClient`     | `false`           | When true, uses `RealIbClient` (requires `lib/IBApi.dll`).   |
| `InteractiveBrokers:ReconnectInitialDelaySeconds` | `1`    | Initial backoff.                                             |
| `InteractiveBrokers:ReconnectMaxDelaySeconds`     | `30`   | Cap on backoff.                                              |
| `Logging:MinimumLevel`                 | `Information`     | `Verbose` / `Debug` / `Information` / `Warning` / `Error`.   |
| `Logging:FilePath`                     | `logs/terminal-.log` | Daily rolling, relative to the app's working directory.   |

Override anything per-machine in `appsettings.local.json` (gitignored).

## Adding a new strategy

This is the whole flow — the shell file (`App.xaml.cs`, `MainWindow.xaml`, etc.) gets exactly **one** new line.

1. Create a new project: `src/TradingTerminal.Strategies.MyStrategy/TradingTerminal.Strategies.MyStrategy.csproj`. Reference `Core` and `UI`.
2. Add `MyStrategy : ITradingStrategy`, `MyStrategyView : UserControl`, `MyStrategyViewModel : ViewModelBase`.
3. Add a DI extension:

   ```csharp
   public static IServiceCollection AddMyStrategy(this IServiceCollection services)
   {
       services.AddSingleton<ITradingStrategy, MyStrategy>();
       services.AddTransient<MyStrategyViewModel>();
       services.AddTransient<MyStrategyView>();
       services.AddSingleton(new StrategyFactoryRegistration(
           StrategyId: "my.strategy.id",
           ViewFactory: sp => sp.GetRequiredService<MyStrategyView>(),
           ViewModelFactory: sp => sp.GetRequiredService<MyStrategyViewModel>()));
       return services;
   }
   ```

4. In `src/TradingTerminal.App/App.xaml.cs` `ConfigureServices`, add **one line**:

   ```csharp
   services.AddMyStrategy();
   ```

5. Add the project as a reference of `TradingTerminal.App` (one csproj entry).

That's it — the new strategy shows up in the left pane on next launch.

## Solution layout

```
TradingTerminal/
├── src/
│   ├── TradingTerminal.App                 WPF entry, DI, MainWindow, shell
│   ├── TradingTerminal.Core                Domain models + interfaces (no UI/IB deps)
│   ├── TradingTerminal.Infrastructure      IbClient (real + fake), repository, ConnectionManager
│   ├── TradingTerminal.UI                  ViewModelBase, dark theme, in-memory log sink
│   └── TradingTerminal.Strategies.Example  Example NVDA strategy (view + vm)
└── tests/
    └── TradingTerminal.Tests               xUnit + FluentAssertions + NSubstitute
```

Project references form an acyclic graph:

```
App   → Infrastructure, UI, Strategies.Example, Core
Strat → UI, Core
Infra → Core
UI    → Core
Core  → (nothing)
```

See `docs/architecture.md` for the full design rationale and key interface signatures.

## Tests

```powershell
dotnet test
```

Six tests covering:

- `StrategyFactory` registers and resolves a strategy + sets `DataContext`.
- `StrategyFactory` throws on unknown ids.
- `MarketDataRepository.SubscribeBarsAsync` propagates the underlying client's "not connected" error.
- `ExampleStrategyViewModel` appends streamed bars to the observable collection.
- `ExampleStrategyViewModel` evicts oldest bar when capacity is exceeded.
- `ConnectionManager` reconnects after the underlying client drops.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| App launches but the banner is permanently red | TWS isn't running, or its socket port doesn't match `appsettings.json`. Default TWS Paper is **7497**. |
| `IB error 502: Couldn't connect to TWS` | API mode not enabled in TWS, OR a Windows firewall block. Enable **API → Settings → Enable ActiveX and Socket Clients** and add `127.0.0.1` to trusted IPs. |
| `IB error 326: Unable to connect as the client id is already in use` | Change `InteractiveBrokers:ClientId` to a value not used by any other client (Excel sheet, Bookmap, another instance, etc.). |
| Build error referencing `IBApi` | You set `UseRealClient = true` but didn't drop `IBApi.dll` into `lib/`. Either copy the DLL or set the flag back to `false`. |
| Chart shows synthetic random-walk bars | You're on `FakeIbClient` (the default). Set `UseRealClient = true` and place `lib/IBApi.dll`. |
| `dotnet build` complains about a missing .NET 8 SDK | This project targets `net9.0-windows` (only .NET 9 is installed on the dev box). The framework choice is documented under Assumptions in `docs/architecture.md`. |
| Tests fail with a STA error | Make sure the `Xunit.StaFact` package restored — the WPF-touching test uses `[WpfFact]`, which spins up an STA thread. |

## Assumptions

- **`net9.0-windows` instead of `net8.0-windows`** — only the .NET 9 SDK is installed on the dev box. WPF works identically. If you need .NET 8, edit `Directory.Build.props` and install the .NET 8 SDK.
- **Synthetic data by default** — the build is green without `IBApi.dll`, and the app runs with `FakeIbClient` (random-walk bars). This was a deliberate trade-off so first-run works with zero IB setup. Real data is one config flag + one DLL away.
- **Single account / no orders in v1** — the strategy is read-only (charting). No order routing, no position management, no PnL. Hooks (`IbClient` callbacks for orders / positions) are stubbed.
- **Bar cadence in `FakeIbClient`** — synthetic bars tick once per second (regardless of the configured timeframe) so the chart is visibly "live" when you're demoing.

## License

MIT — see [LICENSE](LICENSE).
