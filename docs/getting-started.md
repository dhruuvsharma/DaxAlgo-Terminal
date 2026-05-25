# Getting started

> Last updated: 2026-05-25

The shortest path from a clean clone to a running shell. For broker-specific configuration after the first launch, see [brokers.md](brokers.md). For the daily-use walkthrough, see [user-guide.md](user-guide.md).

## Prerequisites

| Tool | Version |
|---|---|
| Windows | 10 or 11 |
| .NET SDK | 9.x (target framework is `net9.0-windows`) |
| Git | any recent version |

You do **not** need any broker installed to build and run — the synthetic `Fake*Client` random-walks run out of the box for IB, NinjaTrader, and cTrader. Alpaca has no synthetic fallback, so the Alpaca tile only works once credentials are filled in.

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

The login window opens with four broker tiles: Interactive Brokers, NinjaTrader, cTrader, Alpaca. Pick one, fill in the form, and click **Sign in**. On success, the main shell opens.

Quick smoke test without any broker:

1. Pick the **Interactive Brokers** tile.
2. Leave the host/port at defaults.
3. Untick `UseRealClient` in `appsettings.json` first (so `FakeIbClient` is wired), or just connect anyway and let the IB connect fail — the rest of the app still launches.
4. Double-click any strategy in the left pane to open it and watch synthetic ticks flow.

## Repo layout (at a glance)

```
DaxAlgo Terminal/
├── src/                                  C# source
│   ├── TradingTerminal.App               WPF entry, DI bootstrap, MainWindow, LoginWindow
│   ├── TradingTerminal.Backtest.Cli      Headless backtest runner (daxalgo-backtest.exe)
│   ├── TradingTerminal.Core              Domain models + interfaces (no UI/broker deps)
│   ├── TradingTerminal.Infrastructure    Broker clients, repository, backtest engine, store
│   ├── TradingTerminal.UI                Theme, base view-models, dock helpers
│   └── TradingTerminal.Strategies.*      One project per strategy (20+ of them)
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
