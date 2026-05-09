# DaxAlgo Terminal

[![.NET 9](https://img.shields.io/badge/.NET-9.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![WPF](https://img.shields.io/badge/UI-WPF%20%2B%20MahApps%20%2B%20AvalonDock-blueviolet)](#)
[![Brokers](https://img.shields.io/badge/Brokers-IB%20%7C%20NinjaTrader%20%7C%20cTrader-orange)](#brokers)

A modular **multi-broker** WPF trading terminal that hosts strategies as plug-ins inside a dockable shell. Picks a broker at login (Interactive Brokers, NinjaTrader, or cTrader) and routes everything downstream — historical bars, live ticks, connection state, reconnect logic — through a single `IBrokerClient` seam. Ships with two strategies: **RSI Overbought/Oversold** and **Cumulative Delta Scalper**.

The repo is structured as an honest engineering exercise: clean MVVM, plug-in architecture, end-to-end async streaming, broker-neutral abstractions over three very different transports (TCP socket, P/Invoke, TLS+protobuf), and a testable threading model.

## Highlights

- **Strict MVVM** with `CommunityToolkit.Mvvm` source generators — zero business logic in code-behind.
- **Plug-in strategy host** — adding a strategy is a new project + one DI line. The shell never references strategy concretes.
- **Three broker backends behind one interface:**
  - **Interactive Brokers** — official TWS API (`EClientSocket`/`EWrapper`), auto-resolved from any standard install path.
  - **NinjaTrader 8** — `NTDirect.dll` via P/Invoke (ANSI C ABI).
  - **cTrader** — Spotware Open API 2.0 over TLS + protobuf (`cTrader.OpenAPI.Net` package).
- **Broker selector at login.** Three-tile UI; the user's choice flips an `IBrokerSelector` singleton, the connection manager re-wires, and the rest of the app stays unaware of which broker is actually in play.
- **Async streaming** via `IAsyncEnumerable<Bar>` and `IAsyncEnumerable<Tick>` with `[EnumeratorCancellation]` — cancellation is the natural unsubscribe path.
- **Threading is a one-layer concern.** Broker callbacks are marshalled to the UI dispatcher inside the repository; view-models stay single-threaded from their POV.
- **Auto-reconnect** with exponential backoff (1 s → 30 s cap), surfaced as a red banner with a Reconnect button.
- **Reactive connection state** — `IObservable<ConnectionState>` flows from the connection manager through the repository to view-models. No polling.
- **Credential safety** — IB password and cTrader OAuth secrets are stored DPAPI-encrypted under `DataProtectionScope.CurrentUser`.
- **AvalonDock** layout (left: strategies pane, center: strategy tabs, bottom: pinned logs, status bar with live broker mode badge).
- **ScottPlot 5** candlestick chart (auto-scrolling, last ~200 bars, configurable timeframe).
- **Logs pane** wired through Serilog with a custom in-memory sink.
- **xUnit + FluentAssertions + NSubstitute** tests — broker-agnostic via the `IBrokerClient` seam, including a `[WpfFact]` STA test for WPF-touching code.

## Brokers

| Broker | Transport | Real client status | Notes |
|---|---|---|---|
| **Interactive Brokers** | TCP socket → `EClientSocket` | ✅ when `CSharpAPI.dll` is found at build time (auto-discovers the standard `C:\TWS API\…` install) | TWS/IB Gateway must be running and signed in. 2FA is handled by TWS, not by this app. Best stocks/options/futures coverage. |
| **NinjaTrader 8** | `NTDirect.dll` P/Invoke (ANSI) | ✅ when `NTDirect.dll` is found at build time + `UseRealClient=true` | NT 8 must be running with **Tools → Options → AT Interface → AT Interface enabled**. NTDirect doesn't expose historical bars (we synthesize) or L1 sizes. Real-time prices, volume, and `Command(...)`-style order routing work. |
| **cTrader** | TLS + protobuf to `demo.ctraderapi.com` / `live.ctraderapi.com` | ✅ always wired (NuGet package always restores) | Requires OAuth setup at [connect.spotware.com/apps](https://connect.spotware.com/apps): clientId + clientSecret + accessToken + ctidTraderAccountId. Real `ProtoOAGetTrendbarsReq` history, push-based `ProtoOASpotEvent` ticks, full symbol catalog. |

When the real client for a given broker isn't wired, the synthetic `Fake*Client` runs instead — a plausible random-walk that lets you exercise the UI and strategies with zero broker setup.

## Architecture at a glance

```
┌───────────────────────────────────────────────────────────────────────────┐
│                            TradingTerminal.App                            │
│  LoginWindow (broker selector) → MainWindow (AvalonDock + strategy host)  │
└──────────────┬───────────────────────────────────┬────────────────────────┘
               │                                   │
               ▼                                   ▼
   ┌───────────────────────┐         ┌──────────────────────────────┐
   │ TradingTerminal.UI    │         │ TradingTerminal.Strategies.* │
   │ ViewModelBase, theme  │         │ RSI, CumulativeDelta, ...    │
   └───────────┬───────────┘         └──────────────┬───────────────┘
               │                                    │
               └───────────────┬────────────────────┘
                               ▼
            ┌─────────────────────────────────────────┐
            │  TradingTerminal.Infrastructure         │
            │  ConnectionManager (re-wires on switch) │
            │  MarketDataRepository  ──>  marshals    │
            │                              callbacks  │
            │                              to UI      │
            │  ┌─────────────────────────────────┐    │
            │  │      IBrokerSelector            │    │
            │  │  Active ──> IBrokerClient       │    │
            │  └─┬───────────┬───────────┬───────┘    │
            │    ▼           ▼           ▼            │
            │  RealIb     RealNinja    RealCTrader    │
            │  FakeIb     FakeNinja    FakeCTrader    │
            └────────────────┬────────────────────────┘
                             ▼
              ┌────────────────────────────────┐
              │  TradingTerminal.Core          │
              │  Bar, Tick, Contract,          │
              │  IBrokerClient, BrokerKind,    │
              │  IMarketDataRepository,        │
              │  ITradingStrategy, IEventBus   │
              └────────────────────────────────┘
```

Project reference graph (acyclic):

```
App        → Infrastructure, UI, Strategies.*, Core
Strategies → UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

`Core` has zero deps on UI / WPF / IB / NT / cTrader. Adding a new broker = a new `IBrokerClient` implementation in `Infrastructure/<Broker>/` and one DI registration block. Adding a new strategy = a new project + one DI line in `App.xaml.cs`. The shell stays untouched.

See `docs/architecture.md` for the full design rationale and key interface signatures.

## Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 9.x (target framework: `net9.0-windows`) |
| Windows | 10 / 11 |
| **At least one of:** | |
| Interactive Brokers TWS / IB Gateway | Installed + signed in. Paper or live. |
| NinjaTrader 8 | Installed + signed in, AT Interface enabled. |
| cTrader-compatible broker account | Plus a registered Spotware app (OAuth credentials). |

You don't need any of the brokers to build and run — the synthetic clients work out of the box.

## Run it

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/TradingTerminal.App -c Release
```

Login screen → pick a broker tile → fill in the form → **Sign in**. On success, the main shell opens. Double-click a strategy in the left pane to open it.

## Configure each broker

The `appsettings.json` at the repo root sets per-broker defaults. Per-machine overrides go in `appsettings.local.json` (gitignored).

### Interactive Brokers

The TWS API isn't on nuget.org. The build auto-resolves `CSharpAPI.dll` from any of the following (first match wins):

1. `lib/CSharpAPI.dll` (or `lib/IBApi.dll` for older copies) at the repo root
2. `$(TwsApiClientDll)` MSBuild property — `dotnet build -p:TwsApiClientDll="D:\some\path\CSharpAPI.dll"`
3. `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll` — the standard installer location

If at least one resolves, the build prints `IB CSharpAPI resolved from: <path>` and `RealIbClient` is compiled in.

In **TWS → File → Global Configuration → API → Settings**:
- ✅ Enable ActiveX and Socket Clients
- ✅ Read-Only API (recommended; the included strategies are read-only)
- Socket port: `7497` (TWS Paper) / `7496` (TWS Live) / `4002` (Gateway Paper) / `4001` (Gateway Live)
- Trusted IPs: add `127.0.0.1`

Then in `appsettings.json`:

```json
"InteractiveBrokers": {
  "Host": "127.0.0.1",
  "Port": 7497,
  "ClientId": 1,
  "AccountType": "Paper",
  "UseRealClient": true,
  "MarketDataType": 1
}
```

`MarketDataType` accepts `1` (Live) / `3` (Delayed, free, ~15 min lag) / `4` (Delayed-Frozen). Switch to `3` if you see IB error 10089.

### NinjaTrader 8

The build auto-resolves `NTDirect.dll` from:

1. `lib/NTDirect.dll`
2. `$(NinjaTraderApiDll)` MSBuild prop
3. `%USERPROFILE%\Documents\NinjaTrader 8\bin64\NTDirect.dll` (standard install)

When found, the build prints `NTDirect resolved from: <path>` and copies the DLL next to the assembly so P/Invoke finds it. Then in `appsettings.json`:

```json
"NinjaTrader": {
  "AccountName": "Sim101",
  "DefaultFuturesContractMonth": "06-26",
  "UseRealClient": true
}
```

Make sure NT 8 is running with **Tools → Options → AT Interface → 'AT Interface enabled'** before signing in.

### cTrader

Always wired — the `cTrader.OpenAPI.Net` NuGet package is referenced unconditionally. You only need OAuth credentials.

1. Register an app at [connect.spotware.com/apps](https://connect.spotware.com/apps) → note the **Client ID** and **Client Secret**.
2. Run the OAuth flow ([Spotware docs](https://help.ctrader.com/open-api/account-authentication/)) to get an **access token** for your trading account.
3. Find your **ctidTraderAccountId** by sending `ProtoOAGetAccountListByAccessTokenReq` with the access token (or check the Spotware portal).
4. Paste the four values into the cTrader form on the login screen and tick **Use live endpoint** if you want production.

Tokens are DPAPI-encrypted on disk. Access tokens expire (default ~30 days) — refresh and paste the new value when that happens.

The corresponding `appsettings.json` keys (overridable but the login screen is the normal entry path):

```json
"CTrader": {
  "Host": "demo.ctraderapi.com",
  "Port": 5035,
  "IsLive": false
}
```

## Configuration reference (`appsettings.json`)

| Key | Default | Notes |
|---|---|---|
| `InteractiveBrokers:Host` | `127.0.0.1` | |
| `InteractiveBrokers:Port` | `7497` | TWS Paper. Use 7496 / 4001 / 4002 as applicable. |
| `InteractiveBrokers:ClientId` | `1` | Must be unique across all clients connected to the same TWS. |
| `InteractiveBrokers:AccountType` | `Paper` | Cosmetic. |
| `InteractiveBrokers:UseRealClient` | `true` | Set to `false` to force `FakeIbClient` even when CSharpAPI.dll is present. |
| `InteractiveBrokers:MarketDataType` | `1` | 1 Live / 3 Delayed / 4 Delayed-Frozen. |
| `InteractiveBrokers:ReconnectInitialDelaySeconds` | `1` | Initial backoff. |
| `InteractiveBrokers:ReconnectMaxDelaySeconds` | `30` | Cap on backoff. |
| `NinjaTrader:AccountName` | `Sim101` | NT account to drive (default sim). |
| `NinjaTrader:DefaultFuturesContractMonth` | (empty) | Appended to bare futures symbols, e.g. `ES 06-26`. |
| `NinjaTrader:UseRealClient` | `false` | Set to `true` after dropping NTDirect.dll into a resolvable path. |
| `CTrader:Host` | `demo.ctraderapi.com` | Or `live.ctraderapi.com`. |
| `CTrader:Port` | `5035` | TLS port for both endpoints. |
| `CTrader:IsLive` | `false` | Cosmetic — the host string above is what actually routes. |
| `Logging:MinimumLevel` | `Information` | `Verbose` / `Debug` / `Information` / `Warning` / `Error`. |
| `Logging:FilePath` | `logs/terminal-.log` | Daily rolling, relative to the app's working directory. |

OAuth secrets and passwords are not in `appsettings.json` — they live in a DPAPI-encrypted `connection.json` under `%LOCALAPPDATA%\DaxAlgoTerminal\`.

## Adding a new strategy

1. Create a new project: `src/TradingTerminal.Strategies.MyStrategy/`. Reference `Core` and `UI`.
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

4. In `App.xaml.cs` `ConfigureServices`, add **one line**: `services.AddMyStrategy();`.
5. Add the project as a reference of `TradingTerminal.App`.

The new strategy shows up in the left pane on next launch.

## Adding a new broker

1. Add a `BrokerKind` enum value in `Core/Brokers/BrokerKind.cs`.
2. Add `XxxOptions` in `Core/Configuration/`.
3. Implement `IBrokerClient` (`Core/MarketData/IBrokerClient.cs`) in `Infrastructure/Xxx/`. Provide both:
   - `RealXxxClient` — the actual integration. Gate behind a compile-time constant if it depends on a sideloaded DLL (mirror the `HAS_IBAPI` / `HAS_NTAPI` pattern in `Infrastructure.csproj`).
   - `FakeXxxClient` — a synthetic fallback so the build is always green.
4. Register both `IBrokerClient` and `BrokerConnectionMode` for the new broker in `DependencyInjection.cs`. The `BrokerSelector` auto-discovers them via `IEnumerable<IBrokerClient>`.
5. Add a third tile + form panel to `LoginWindow.xaml` and a corresponding `SelectXxx` command + form fields to `LoginViewModel`.

The `MarketDataRepository`, `ConnectionManager`, all view-models, and every strategy stay untouched — they talk to `IBrokerClient` exclusively.

## Solution layout

```
TradingTerminal/
├── src/
│   ├── TradingTerminal.App                       WPF entry, DI bootstrap, MainWindow, LoginWindow
│   ├── TradingTerminal.Core                      Domain models + interfaces (no UI/broker deps)
│   │   ├── Brokers/                              BrokerKind, IBrokerSelector, BrokerConnectionMode
│   │   ├── MarketData/                           IBrokerClient, IMarketDataRepository
│   │   ├── Domain/                               Bar, Tick, Contract, BarSize, ConnectionState
│   │   ├── Strategies/                           ITradingStrategy, IStrategyFactory, StrategyHost
│   │   ├── Configuration/                        InteractiveBrokers/NinjaTrader/CTrader options
│   │   ├── Events/                               IEventBus, EventBus
│   │   └── Session/                              SessionContext
│   ├── TradingTerminal.Infrastructure
│   │   ├── Brokers/                              BrokerSelector
│   │   ├── Ib/                                   RealIbClient (#if HAS_IBAPI), FakeIbClient, ConnectionManager
│   │   ├── NinjaTrader/                          RealNinjaClient (#if HAS_NTAPI), FakeNinjaClient
│   │   ├── CTrader/                              RealCTraderClient, FakeCTraderClient
│   │   ├── MarketData/                           MarketDataRepository
│   │   └── Threading/                            IUiDispatcher, WpfDispatcher
│   ├── TradingTerminal.UI                        ViewModelBase, dark theme, log sink
│   ├── TradingTerminal.Strategies.Rsi            RSI Overbought/Oversold strategy
│   └── TradingTerminal.Strategies.CumulativeDelta  Cumulative Delta Scalper strategy
└── tests/
    └── TradingTerminal.Tests                     xUnit + FluentAssertions + NSubstitute
```

## Tests

```powershell
dotnet test
```

Coverage:
- `StrategyFactory` registers and resolves a strategy + sets `DataContext`.
- `StrategyFactory` throws on unknown ids.
- `MarketDataRepository.SubscribeBarsAsync` propagates the underlying client's "not connected" error (via an `IBrokerClient` substitute through the broker selector).
- `ConnectionManager` reconnects after the underlying client drops, with backoff, and re-wires when the active broker changes.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Login banner permanently red (IB) | TWS isn't running, or its socket port doesn't match `appsettings.json`. Default TWS Paper is **7497**. |
| `IB error 502: Couldn't connect to TWS` | API mode not enabled in TWS. Enable **API → Settings → Enable ActiveX and Socket Clients** and add `127.0.0.1` to trusted IPs. |
| `IB error 326: client id is already in use` | Change `InteractiveBrokers:ClientId` to a value not used by any other client (Excel sheet, Bookmap, another instance). |
| `IB error 10089: requires additional subscription` | You don't have a real-time market-data sub on that contract. Switch `MarketDataType` to `3` (Delayed) in the login form. |
| Chart shows synthetic random-walk bars (IB) | `RealIbClient` wasn't compiled in (no `IB CSharpAPI resolved from:` line at build). Drop `CSharpAPI.dll` into `lib/` or install TWS API to its standard path. |
| NinjaTrader connect fails with `rc != 0` | NT 8 isn't running, or Tools → Options → AT Interface isn't enabled, or `UseRealClient` is false. |
| NinjaTrader connect throws `DllNotFoundException` | NTDirect.dll wasn't copied to the output directory. Confirm the build printed `NTDirect resolved from:`. |
| cTrader connect immediately fails | One of `ClientId` / `ClientSecret` / `AccessToken` / `CtidTraderAccountId` is missing or wrong. Check the Logs pane for the exact `ProtoOAErrorRes` description. |
| cTrader was working, now suddenly fails | The access token has likely expired (~30 days). Re-run the OAuth refresh and paste the new token into the login form. |
| `dotnet build` complains about missing .NET 8 SDK | This project targets `net9.0-windows`. Make sure the .NET 9 SDK is installed. |
| Tests fail with a STA error | The `Xunit.StaFact` package didn't restore. The WPF-touching test uses `[WpfFact]` which spins up an STA thread. |

## Engineering notes / assumptions

- **`net9.0-windows`** — only the .NET 9 SDK is installed on the dev box. WPF works identically on net9.
- **Synthetic data by default** — every broker has a `Fake*Client` so the build is always green, with or without the real broker installed. Real data is one config flag (or one form field) away.
- **Single-account, read-mostly v1** — the included strategies are read-only (charting + signals). Order routing scaffolding is in place (cTrader + NinjaTrader expose order verbs through `IBrokerClient` extensions); the strategies don't yet emit orders.
- **No defensive null-checking on internal calls** — boundaries are validated (config load, broker callbacks, user input); internals trust the type system.
- **No `ConfigureAwait(false)` in WPF view-model code** — we *want* to resume on the UI thread. `ConfigureAwait(false)` is reserved for pure-library code if any is added.
- **`Core` has zero deps** — not on UI, not on WPF, not on any broker SDK. New abstractions go through `Core`; new SDK calls go through `Infrastructure`.

## License

MIT — see [LICENSE](LICENSE).
