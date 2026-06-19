# Getting started

> Last updated: 2026-06-19

The shortest path from a clean clone to a running shell. For broker-specific configuration after the first launch, see [brokers.md](brokers.md). For the daily-use walkthrough, see [user-guide.md](user-guide.md).

## Prerequisites

| Tool | Version |
|---|---|
| Windows | 10 or 11 |
| .NET SDK | 9.x (target framework is `net9.0-windows`) |
| Git | any recent version |

You do **not** need any broker account to build and run. Two zero-credential paths give you data out of the box:

- **`Binance` (real, live data)** — the **Binance (no login)** tile streams real crypto bars / L1 / **L2 depth** / trades over Binance's public WebSocket with no API key and no account. Just click Connect. (Crypto only; geo-blocked regions repoint the host — see [brokers.md](brokers.md#binance-public-market-data-no-key).)
- **`Simulated` (fully offline)** — an in-process synthetic random-walk feed, or replay of your local store; no network, no Docker. The quickest offline launch is the **`Dev: Simulated (offline)`** profile below, which skips login entirely.

The four account-based broker tiles (IB / NinjaTrader / cTrader / Alpaca) only connect once their SDK is wired and credentials are filled in.

Optional:

- **Docker Desktop** — only if you want the PostgreSQL + TimescaleDB market-data backend. SQLite is the default and needs nothing.
- **TWS API** (`CSharpAPI.dll`) — only if you want the real Interactive Brokers client. Auto-discovered from `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\`.
- **NTDirect.dll** — only if you want the real NinjaTrader 8 client. Auto-discovered from `%USERPROFILE%\Documents\NinjaTrader 8\bin64\`.

## Clone and build

```powershell
git clone https://github.com/dhruuvsharma/DaxAlgo-Terminal.git
cd "DaxAlgo Terminal"
dotnet restore
dotnet build -c Release
```

Successful build prints, when applicable:

- `IB CSharpAPI resolved from: <path>` — the real IB client is compiled in (`HAS_IBAPI`).
- `NTDirect resolved from: <path>` — the real NT client is compiled in (`HAS_NTAPI`).

cTrader and Alpaca are always compiled in (NuGet packages — no DLL gate).

## Run

```powershell
dotnet run --project src/TradingTerminal.App -c Release
```

The login window opens with broker tiles: Interactive Brokers, NinjaTrader, cTrader, Alpaca, Ironbeam, London Strategic Edge, and the keyless Binance feed. Connect one or more (sessions are concurrent), and the main shell opens. Tick **Auto Connect** to have every broker with saved credentials connect automatically on future launches.

### Dev launch profiles (skip login, run offline)

For development the fastest path is a launch profile that bypasses login and auto-connects the `Simulated` broker. `src/TradingTerminal.App/Properties/launchSettings.json` defines them; each is selected by `DOTNET_ENVIRONMENT`, which layers an `appsettings.{Env}.json` (repo root) over `appsettings.json`.

| Profile | `DOTNET_ENVIRONMENT` | Behaviour |
|---|---|---|
| `App (Login)` | *(none)* | Normal — login window shown. |
| `Dev: Simulated (offline)` | `DevSim` | No login; `Simulated` broker, **Synthetic** random-walk. Fully offline (SQLite, no Docker/network). |
| `Dev: Replay (local DB)` | `DevReplay` | No login; `Simulated` broker, **Replay** of the local store (10× clock), synthetic fallback where no data. |
| `Dev: Live (no login)` | `DevLive` | No login; auto-connects a real broker (default IB) using saved credentials. |

Pick one from the Visual Studio debug-target dropdown, or set the environment from the shell:

```powershell
$env:DOTNET_ENVIRONMENT = "DevSim"; dotnet run --project src/TradingTerminal.App
```

Then double-click any strategy card in the catalog to open it in its own window and watch ticks flow. These dev files are off in the shipped build. See [configuration.md](configuration.md#dev-launch-profiles) for the `Dev` / `SimulatedBroker` keys.

## Repo layout (at a glance)

```
DaxAlgo Terminal/
├── src/                                  C# source
│   ├── TradingTerminal.App               WPF entry, DI bootstrap, MainWindow, LoginWindow
│   ├── TradingTerminal.Backtest.Cli      Headless backtest runner (daxalgo-backtest.exe)
│   ├── TradingTerminal.Core              Domain models + interfaces (no UI/broker deps)
│   ├── TradingTerminal.MarketData        Canonical pipeline (hub, ingest, store, registry, archive)
│   ├── TradingTerminal.Infrastructure    Broker clients, backtest engine, notifications, regime
│   ├── TradingTerminal.UI                Theme, base view-models, universal activity-log sink, shared controls
│   ├── TradingTerminal.<Tool>            One project per tool/chart/ML/AI window (opens as its own window)
│   └── TradingTerminal.Strategies.*      One project per strategy (12 of them)
├── tests/TradingTerminal.Tests           xUnit + FluentAssertions + NSubstitute
├── tools/
│   ├── cpp-backtester/                   C++ tick backtester (subprocess sidecar)
│   └── python-ml/                        Python AI Market Analyst (FastAPI sidecar)
├── docs/                                 this folder
├── scripts/                              boilerplate generators
└── appsettings.json                      default configuration
```

The full project graph and layering rules are in [architecture.md](architecture.md).

## What to read next

- New user wanting to use the app → [user-guide.md](user-guide.md).
- Configuring a real broker → [brokers.md](brokers.md).
- Tuning configuration → [configuration.md](configuration.md).
- Adding a feature → [contributing.md](contributing.md), then [architecture.md](architecture.md) for the constraints you must not break.
