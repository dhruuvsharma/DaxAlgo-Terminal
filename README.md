# DaxAlgo Terminal

> Last updated: 2026-06-13

[![.NET 9](https://img.shields.io/badge/.NET-9.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![WPF](https://img.shields.io/badge/UI-WPF%20%2B%20MahApps%20%2B%20AvalonDock-blueviolet)](#)
[![Brokers](https://img.shields.io/badge/Brokers-IB%20%7C%20NinjaTrader%20%7C%20cTrader%20%7C%20Alpaca%20%7C%20Ironbeam%20%7C%20LSE%20%7C%20Binance-orange)](#)

A modular **multi-broker** WPF trading terminal that hosts strategies as plug-ins inside a dockable Bloomberg-style shell. Connect one or more brokers at login (Interactive Brokers, NinjaTrader 8, cTrader, Alpaca, Ironbeam, London Strategic Edge, or the keyless Binance feed — sessions are concurrent, with an **Auto Connect** option) and everything downstream — historical bars, live ticks, depth, trade tape, connection state, reconnect logic — routes through a single `IBrokerClient` seam. **Data and signals only — no live order execution.**

![DaxAlgo Terminal main window](images/mainwindow.png)

## What ships

- **10 live strategies** behind one `IBacktestStrategy` plug-in seam — the APEX tape-primary microstructure scalper, Ornstein-Uhlenbeck mean reversion, volatility-targeted index baseline, L2/DOM order-flow (VPIN toxicity, cumulative delta with footprint clusters, order-flow pressure map over the S&P 100/500), and the 3D regime-cube family (Order-Flow Cube, Order-Flow Surface Spike, Imbalance Heat Front, Index K-Score Surface). Plus buy-and-hold / mean-reversion / Donchian demos in the backtester.
- **Seven broker backends** behind one `IBrokerClient`: IB (TWS API), NT 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0), Alpaca (REST + WebSocket), Ironbeam futures (REST + WebSocket API v2), London Strategic Edge (free multi-asset L1 + history), and keyless Binance public data — plus the always-registered offline `Simulated` broker.
- **Charts & order-flow windows** — TradingView-style charts (WebView2), live L2 order-book ladder, a bid/ask **volume footprint** with seven toggleable POC regression fits (linear → Theil–Sen → LOWESS) and a virtual fit-consensus predictor, six heatmaps (Bookmap-style depth, imbalance, volume-at-price, bubbles, cross-asset vol, rolling correlation), and live/static correlation matrices.
- **Machine Learning menu** — stationarity & differencing lab (ADF/KPSS/ACF, fractional differencing), ARIMA + GARCH forecasting with confidence bands, and Kalman filters (local level / trend / time-varying pairs hedge-β) — all over historical bars from the local store.
- **Canonical market-data pipeline** — broker-neutral `InstrumentId`, Rx fanout hub, ref-counted tick-primary ingest, and a three-backend store (embedded SQLite by default, PostgreSQL + TimescaleDB via `docker compose`, or QuestDB for the high-rate surfaces). Postgres auto-falls-back to SQLite when unreachable. Optional Telegram archive offloader so the local store can prune safely.
- **Tick-level backtest engine** — `IFeeModel` (zero / maker-taker / bps), `IRiskManager` (per-symbol cap + daily PnL cap), L1 fill model, ParquetTick reader/writer, full stats suite (Sharpe, Sortino, Calmar, Omega, Ulcer, recovery, max consec losses). Headless CLI with `run` / `sweep` / `walkforward` / `mc` / `tca` / `features` subcommands.
- **Notifications** — bounded `Channel<>` + hosted dispatcher fans signals out to Telegram (Bot API) and Discord (channel webhook), with an optional Ollama LLM commentary enricher.
- **AI Market Analyst** — four-agent LangGraph (indicator → pattern → trend → decision) in a Python sidecar over loopback HTTP/JSON. Provider-agnostic (OpenAI / Anthropic / Qwen / MiniMax). Renders annotated candlestick + trend-channel charts; degrades gracefully when the sidecar isn't running.
- **Market regime suite** — a 0–100 risk-on / risk-off composite blended from 10 sub-signals across Yahoo Finance / FRED / CNN Fear & Greed / AAII (with an optional signal gate), plus per-instrument, Markov transition-matrix, and an 18-indicator × 8-timeframe **Advanced regime dashboard**.
- **Bloomberg-style shell** — MahApps Metro chrome, AvalonDock VS2013 Dark theme, Consolas monospace throughout, amber accent on pure-black canvas, full-height strategies pane, status-bar mode badge.

## Quick start

```powershell
git clone https://github.com/dhruuvsharma/DaxAlgo-Terminal.git
cd "DaxAlgo Terminal"
dotnet restore
dotnet build -c Release
dotnet run --project src/TradingTerminal.App -c Release
```

You don't need any broker account to build and run. The **`Binance`** tile streams real, live crypto data (bars, L1, **L2 depth**, trades) over Binance's public WebSocket with **no API key and no account** — just click Connect. For a fully-offline run, the always-registered **`Simulated` broker** serves a synthetic random-walk feed (or replay of your local store); the dev launch profiles (`Dev: Simulated (offline)` etc.) even skip the login window.

For setup details (DLL resolution, port numbers, OAuth flow, API keys, the keyless Binance feed, dev launch profiles), see [docs/getting-started.md](docs/getting-started.md) and [docs/brokers.md](docs/brokers.md).

## Screenshots

| | |
|---|---|
| ![Login](images/loginscreenwindow.png) | ![APEX scalper](images/apexmicrostructurescalperwindow.png) |
| Multi-broker login | APEX microstructure scalper |
| ![Order-Flow Cube](images/imbalanceheatfrontwindow.png) | ![Index K-Score Surface](images/indexkscoresurfacewindow.png) |
| Imbalance Heat Front (3D) | Index K-Score Surface (3D) |
| ![Backtest](images/backtestwindow.png) | ![AI Market Analyst](images/almarketanalystwindow.png) |
| Tick-level backtest | AI Market Analyst |
| ![Market regime](images/marketregimewindow.png) | ![Correlation matrix](images/correlationmatrixwindow.png) |
| Market-regime composite | Correlation matrix |

More screenshots are embedded throughout the focused docs (strategies, brokers, AI analyst, tools).

## Documentation

All documentation lives in [docs/](docs/README.md). Quick links:

| Audience | Start here |
|---|---|
| First-time user | [getting-started.md](docs/getting-started.md), [user-guide.md](docs/user-guide.md) |
| Setting up a broker | [brokers.md](docs/brokers.md) |
| Tuning configuration | [configuration.md](docs/configuration.md) |
| Adding a strategy / broker / notifier | [contributing.md](docs/contributing.md) |
| Backtesting | [backtesting.md](docs/backtesting.md) |
| Feature deep-dive | [docs/README.md](docs/README.md) (index of every focused doc) |
| Architecture | [architecture.md](docs/architecture.md) |
| Something broken | [troubleshooting.md](docs/troubleshooting.md) |

## Project graph

```
App            → MarketData, Infrastructure, UI, Login, Ai, Strategies.*, Core
Login          → Core, UI, Infrastructure
Ai             → Core, UI, Infrastructure, MarketData
Strategies     → Infrastructure, UI, Core
Infrastructure → MarketData, Core
MarketData     → Core
UI             → Core
Core           → (nothing)
```

`Core` has zero deps on UI, WPF, IB, NT, cTrader, or Alpaca. The market-data pipeline (`MarketData`) sits below `Infrastructure`; the login flow (`Login`) and AI/ML tooling (`Ai`) are their own projects so the App shell stays thin. Adding a new broker = a new `IBrokerClient` implementation in `Infrastructure/<Broker>/` and one DI registration block. Adding a new strategy = a new project + one DI line in `App.xaml.cs`. The shell stays untouched.

## License

MIT — see [LICENSE](LICENSE).
