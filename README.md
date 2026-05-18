# DaxAlgo Terminal

[![.NET 9](https://img.shields.io/badge/.NET-9.0--windows-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![WPF](https://img.shields.io/badge/UI-WPF%20%2B%20MahApps%20%2B%20AvalonDock-blueviolet)](#)
[![Brokers](https://img.shields.io/badge/Brokers-IB%20%7C%20NinjaTrader%20%7C%20cTrader%20%7C%20Alpaca-orange)](#brokers)

A modular **multi-broker** WPF trading terminal that hosts strategies as plug-ins inside a dockable shell. Picks a broker at login (Interactive Brokers, NinjaTrader, cTrader, or Alpaca) and routes everything downstream — historical bars, live ticks, connection state, reconnect logic — through a single `IBrokerClient` seam. Ships with two live strategies (**RSI Overbought/Oversold** and **Cumulative Delta Scalper** — sniper-mode port of the cTrader cBot, with a 5-confirmation gate, multi-session GMT filter, and per-session/daily caps), a notifier that fans signals out to **Telegram and Discord**, and a **tick-level backtest engine** with 15+ canonical strategies (HFT/microstructure, FX baselines, S&P 500 baselines) plus a per-symbol risk manager and maker/taker fee model.

The repo is structured as an honest engineering exercise: clean MVVM, plug-in architecture, end-to-end async streaming, broker-neutral abstractions over three very different transports (TCP socket, P/Invoke, TLS+protobuf), and a testable threading model.

## Highlights

- **Strict MVVM** with `CommunityToolkit.Mvvm` source generators — zero business logic in code-behind.
- **Plug-in strategy host** — adding a strategy is a new project + one DI line. The shell never references strategy concretes.
- **Four broker backends behind one interface:**
  - **Interactive Brokers** — official TWS API (`EClientSocket`/`EWrapper`), auto-resolved from any standard install path.
  - **NinjaTrader 8** — `NTDirect.dll` via P/Invoke (ANSI C ABI).
  - **cTrader** — Spotware Open API 2.0 over TLS + protobuf (`cTrader.OpenAPI.Net` package).
  - **Alpaca** — REST (history) + WebSocket (live ticks) via the `Alpaca.Markets` SDK. Stocks + crypto, paper or live, single API key + secret (no OAuth).
- **Broker selector at login.** Four-tile UI; the user's choice flips an `IBrokerSelector` singleton, the connection manager re-wires, and the rest of the app stays unaware of which broker is actually in play.
- **Async streaming** via `IAsyncEnumerable<Bar>` and `IAsyncEnumerable<Tick>` with `[EnumeratorCancellation]` — cancellation is the natural unsubscribe path.
- **Threading is a one-layer concern.** Broker callbacks are marshalled to the UI dispatcher inside the repository; view-models stay single-threaded from their POV.
- **Auto-reconnect** with exponential backoff (1 s → 30 s cap), surfaced as a red banner with a Reconnect button.
- **Reactive connection state** — `IObservable<ConnectionState>` flows from the connection manager through the repository to view-models. No polling.
- **Credential safety** — IB password and cTrader OAuth secrets are stored DPAPI-encrypted under `DataProtectionScope.CurrentUser`.
- **AvalonDock** layout (left: strategies pane, center: strategy tabs, bottom: pinned logs, status bar with live broker mode badge).
- **ScottPlot 5** candlestick chart (auto-scrolling, last ~200 bars, configurable timeframe).
- **Logs pane** wired through Serilog with a custom in-memory sink.
- **Multi-transport notifier** — strategies publish a `StrategyNotification` to an `INotificationPublisher`; a hosted background worker drains a bounded `Channel<>` and fans out to every enabled `INotificationTransport`. Ships with **Telegram** (Bot API) and **Discord** (channel webhook) transports. Configured live from a Settings tab with hot-reload via `IOptionsMonitor`.
- **AI Market Analyst** — multi-agent LangGraph (indicator → pattern → trend → decision) running behind a Python sidecar on `127.0.0.1`. Renders annotated candlestick + trend-channel charts, scores against a 16-pattern classical catalog via a vision LLM, returns a structured `Long`/`Short`/`NoCall` verdict to the WPF pane and optionally appends a one-line summary to every signal notification. Provider-agnostic (OpenAI / Anthropic / Qwen / MiniMax); API key stored DPAPI-encrypted. Degrades gracefully when the sidecar isn't running — UI shows "AI Analyst unavailable", notifications pass through unchanged.
- **Tick-level backtest engine** with a parquet tick store, L1 fill model, simulated order book, `IOrderRouter` seam shared with live, per-symbol `IRiskManager`, pluggable `IFeeModel` (zero / maker-taker / bps), and an extended stats suite (Sharpe, Sortino, **Calmar, Omega, Ulcer index, recovery factor, max consecutive losses, downside deviation**) — plus a **15+ strategy library** spanning HFT/microstructure (Avellaneda-Stoikov MM, microprice, Ornstein-Uhlenbeck), FX baselines (Bollinger, MA crossover, Connors RSI(2), London open breakout, MACD), and S&P 500 baselines (200-SMA trend filter, vol targeting, gap fade, end-of-day momentum, pullback continuation). Headless CLI with a `sweep` subcommand that grids parameters in parallel.
- **xUnit + FluentAssertions + NSubstitute** tests — broker-agnostic via the `IBrokerClient` seam, including a `[WpfFact]` STA test for WPF-touching code. 31+ tests covering indicators, microstructure helpers, fee model, risk manager, backtest engine, broker selector, and strategy factory.

## Brokers

| Broker | Transport | Real client status | Notes |
|---|---|---|---|
| **Interactive Brokers** | TCP socket → `EClientSocket` | ✅ when `CSharpAPI.dll` is found at build time (auto-discovers the standard `C:\TWS API\…` install) | TWS/IB Gateway must be running and signed in. 2FA is handled by TWS, not by this app. Best stocks/options/futures coverage. L2 depth (`reqMktDepth`) is not yet wired — `SubscribeDepthAsync` throws. |
| **NinjaTrader 8** | `NTDirect.dll` P/Invoke (ANSI) | ✅ when `NTDirect.dll` is found at build time + `UseRealClient=true` | NT 8 must be running with **Tools → Options → AT Interface → AT Interface enabled**. NTDirect doesn't expose historical bars (we synthesize) or L1 sizes. Real-time prices, volume, and `Command(...)`-style order routing work. **No L2** — NT's depth lives behind NinjaScript SuperDOM, not the AT Interface. |
| **cTrader** | TLS + protobuf to `demo.ctraderapi.com` / `live.ctraderapi.com` | ✅ always wired (NuGet package always restores) | Requires OAuth setup at [connect.spotware.com/apps](https://connect.spotware.com/apps): clientId + clientSecret + accessToken + ctidTraderAccountId. Real `ProtoOAGetTrendbarsReq` history, push-based `ProtoOASpotEvent` ticks, full symbol catalog. **L2 depth** wired via `ProtoOASubscribeDepthQuotesReq` / `ProtoOADepthEvent` with incremental new/deleted quotes → local book reconstruction → emitted `DepthSnapshot`s. |
| **Alpaca** | REST (history) + WebSocket (live ticks) to `api.alpaca.markets` / `paper-api.alpaca.markets` | ✅ always wired (NuGet `Alpaca.Markets`, no DLL gate) | Single API key id + secret (paper key prefix `PK…`, live `AK…`). One client multiplexes stocks (`Contract.SecType` = `STK`) and crypto (`CRYPTO`); options route is reserved for when the SDK stabilises. Stock data feed selectable: `iex` (free) / `sip` (paid sub). **No L2** — Alpaca only exposes L1 quotes; `SubscribeDepthAsync` throws. OMS not yet wired. |

When the real client for a given broker isn't wired, the synthetic `Fake*Client` runs instead — a plausible random-walk that lets you exercise the UI and strategies with zero broker setup. (Exception: Alpaca has no synthetic fallback — credentials are mandatory to use that tile.)

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
            │              RealAlpaca                 │
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

`Core` has zero deps on UI / WPF / IB / NT / cTrader / Alpaca. Adding a new broker = a new `IBrokerClient` implementation in `Infrastructure/<Broker>/` and one DI registration block. Adding a new strategy = a new project + one DI line in `App.xaml.cs`. The shell stays untouched.

See `docs/architecture.md` for the full design rationale and key interface signatures.
See `docs/user-guide.md` for the end-user manual (login, strategies, notifications,
backtesting, factor research, recorder, CLI).
See `docs/polyglot.md` for the cross-language seam: the C++ tick backtester
(in-tree at `tools/cpp-backtester/`, built separately via CMake, surfaced as
the **Use C++ Fast engine** checkbox in the Backtest tab) and the forthcoming
Python ML sidecar.

## Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 9.x (target framework: `net9.0-windows`) |
| Windows | 10 / 11 |
| **At least one of:** | |
| Interactive Brokers TWS / IB Gateway | Installed + signed in. Paper or live. |
| NinjaTrader 8 | Installed + signed in, AT Interface enabled. |
| cTrader-compatible broker account | Plus a registered Spotware app (OAuth credentials). |
| Alpaca account | API key id + secret minted from the [Alpaca dashboard](https://app.alpaca.markets). Paper accounts are free. |

You don't need any of the brokers to build and run — the synthetic clients work out of the box. (Alpaca is the exception: it has no synthetic fallback, so the Alpaca tile only works once credentials are filled in.)

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

### Alpaca

Always wired — the `Alpaca.Markets` NuGet package is referenced unconditionally. Mint an
API key + secret from the Alpaca dashboard:

- Paper: [app.alpaca.markets](https://app.alpaca.markets) → *Paper trading → API keys → Generate*. Key id starts with `PK…`.
- Live: [app.alpaca.markets/live](https://app.alpaca.markets/live) → *API keys → Generate*. Key id starts with `AK…`. (Funded account required.)

Paste both into the Alpaca tile on the login screen, pick the stock data feed (`iex` is
free; `sip` requires a paid market-data subscription), and tick **Use live endpoint** for
production. The secret is DPAPI-encrypted on disk.

```json
"Alpaca": {
  "ApiKey": "",
  "ApiSecret": "",
  "IsLive": false,
  "StockDataFeed": "iex"
}
```

`Contract.SecType` drives asset-class routing inside `RealAlpacaClient`: `STK` → stock
endpoints, `CRYPTO` → crypto endpoints. Other sec-types (options) throw
`NotSupportedException` until the SDK's options surface stabilises. There is no L2 depth
— Alpaca only exposes L1 quotes.

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
| `Alpaca:ApiKey` | (empty) | Key id from the Alpaca dashboard (`PK…` paper / `AK…` live). |
| `Alpaca:ApiSecret` | (empty) | API secret. Stored DPAPI-encrypted at runtime — the login form is the normal entry path. |
| `Alpaca:IsLive` | `false` | `true` targets `api.alpaca.markets`; `false` targets `paper-api.alpaca.markets`. |
| `Alpaca:StockDataFeed` | `iex` | `iex` (free) or `sip` (paid). Ignored for crypto. |
| `Alpaca:ReconnectInitialDelaySeconds` | `1` | Initial backoff. |
| `Alpaca:ReconnectMaxDelaySeconds` | `30` | Cap on backoff. |
| `Logging:MinimumLevel` | `Information` | `Verbose` / `Debug` / `Information` / `Warning` / `Error`. |
| `Logging:FilePath` | `logs/terminal-.log` | Daily rolling, relative to the app's working directory. |
| `Notifications:QueueCapacity` | `256` | Bounded channel size; oldest dropped on overflow. |
| `Notifications:Telegram:Enabled` | `false` | Master toggle for the Telegram transport. |
| `Notifications:Telegram:BotToken` | (empty) | Token from @BotFather. Edit via the Settings tab — the value is persisted to `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`, which overlays `appsettings.json`. |
| `Notifications:Telegram:ChatId` | (empty) | Numeric chat ID or `@channelname`. |
| `Notifications:Telegram:IncludeIdleSignals` | `false` | When false, drop signals fired below the strategy's armed threshold. |
| `Notifications:Discord:Enabled` | `false` | Master toggle for the Discord transport. |
| `Notifications:Discord:WebhookUrl` | (empty) | Channel webhook URL: *Edit Channel → Integrations → Webhooks → Copy URL*. |
| `Notifications:Discord:Username` | `DaxAlgo Terminal` | Optional username override. Empty = use webhook default. |
| `Notifications:Discord:IncludeIdleSignals` | `false` | Same semantics as the Telegram knob. |

OAuth secrets, passwords, and Alpaca API secrets are not in `appsettings.json` — they live in a DPAPI-encrypted `connection.json` under `%LOCALAPPDATA%\DaxAlgoTerminal\`.

## Notifications (Telegram + Discord)

Strategies publish `StrategyNotification` events to `INotificationPublisher` whenever a signal fires. A hosted background worker drains a bounded `Channel<StrategyNotification>` and fans each message out to every enabled `INotificationTransport`. Two transports ship: **Telegram** (Bot API) and **Discord** (channel webhook), each via its own named `HttpClient`. Both are independent — enable either, both, or neither.

**Telegram setup:** paste the bot token from [@BotFather](https://t.me/BotFather) and a chat ID (numeric for users/groups, or `@channelname` for public channels — get one via [@userinfobot](https://t.me/userinfobot)) into the Telegram block on *Settings → Notifications…*.

**Discord setup:** in the destination Discord channel, *Edit Channel → Integrations → Webhooks → New Webhook → Copy Webhook URL*. Paste that URL into the Discord block in the Settings tab. Optional: set a username override. Tick **Enabled**, **Save**, then **Send test**.

**Persistence:** the Settings tab writes to `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`. That file is layered into the host configuration with `reloadOnChange: true`, so `IOptionsMonitor<NotificationsOptions>` reflects edits live — no restart.

**Defaults** in `appsettings.json`:

```json
"Notifications": {
  "QueueCapacity": 256,
  "Telegram": {
    "Enabled": false,
    "BotToken": "",
    "ChatId": "",
    "IncludeIdleSignals": false
  },
  "Discord": {
    "Enabled": false,
    "WebhookUrl": "",
    "Username": "DaxAlgo Terminal",
    "IncludeIdleSignals": false
  }
}
```

`IncludeIdleSignals` is the toggle for low-confirmation/sub-armed signals — the dashboard surfaces these as `(idle)` lines and each transport drops them by default. The publisher itself doesn't filter — that's the transport's call, so each future channel decides for itself.

**Adding a transport** (e.g. Slack, email, SMS): add a class implementing `INotificationTransport` in `Infrastructure/Notifications/<Channel>/`, plus one DI line in `NotificationsServiceCollectionExtensions.cs`. The dispatcher auto-discovers transports via `IEnumerable<INotificationTransport>`. The Telegram and Discord transports are the reference implementations to mirror.

## Backtesting

A first-class tick-level backtest engine ships with the terminal. Strategies that implement `IBacktestStrategy` run against a parquet tick file through the same `IOrderRouter` seam they'd use live, so the engine measures simulated fills, P&L, equity curve, drawdown, and a broad performance suite.

**Surfaces:**

- **CLI** (`daxalgo-backtest`) — headless, scriptable, three subcommands: `synth` (generate synthetic ticks), `run` (single backtest), `sweep` (parameter grid → CSV).
- **Tools → Backtest** in the WPF shell — strategy picker, run/cancel, ScottPlot equity curve, trades grid, stats panel.

### Strategy library

Fifteen-plus canonical strategies live behind one `IBacktestStrategy` plug-in seam — pickable from the WPF dropdown or via `--strategy <id>` in the CLI:

| Family | Strategy id | What it does |
|---|---|---|
| Demo | `buyAndHold` | Market-buy on the first tick, sell on the last. Engine smoke-test. |
| Demo | `meanReversion` | Rolling-mean reversion with fixed thresholds. |
| Demo | `donchianBreakout` | N-tick Donchian channel break, trailing-mid stop. |
| HFT | `avellanedaStoikov` | Avellaneda-Stoikov optimal market maker (inventory-shifted reservation, online variance EMA, configurable requote cadence). |
| HFT | `microprice` | Size-weighted microprice deviation scalper. |
| HFT | `ornsteinUhlenbeck` | Online AR(1)-fit OU process, z-score entry/exit bands. |
| HFT | `twap` | TWAP parent-order slicer with tail flush. |
| Forex | `bollinger` | Bollinger band reversion (Bollinger 2001). |
| Forex | `maCrossover` | Fast/slow SMA cross, golden/death-cross (Murphy 1999). |
| Forex | `rsi2` | Connors RSI(2) reversion (Connors 2008). |
| Forex | `londonOpen` | Asian-range / London-open breakout with ATR trail (Volman 2011). |
| Forex | `macd` | 12/26/9 MACD signal-line crossover (Appel 2005). |
| Index | `trendFilter` | Long when price > 200-period SMA, else flat (Faber 2007). |
| Index | `volTarget` | Position sized to target_vol / realized_vol_ewma (AQR risk-parity overlay). |
| Index | `gapFade` | Detect overnight gap, fade toward previous close. |
| Index | `eodMomentum` | Take direction of day's open-to-now return in the last N% of the UTC session (Gao-Han-Li-Zhou 2018). |
| Index | `pullback` | Trend filter + N-tick pullback + resumption entry, pct stop/target ("buy the dip"). |
| L2 / DOM | `bookPressure` | Cumulative order-book imbalance signal (Cartea-Jaimungal-Penalva). Trades touch sizes today; generalises to `Microstructure.CumulativeImbalance` over a `DepthSnapshot` when L2 ticks land. |
| L2 / DOM | `liquiditySweep` | Aggressive-flow / sweep detector — rolling-mean depth + same-side price drop. |
| L2 / DOM | `iceberg` | Hidden-liquidity sticky-touch heuristic; trades toward the iceberg-supported side. |
| L2 / DOM | `vpin` | VPIN-style order-flow toxicity (Easley, López de Prado, O'Hara 2012). Mean-reverts against high toxicity. |
| L2 / DOM | `thinBook` | Breakout entry gated by a depth threshold — passes on thin-book setups. |

These are textbook reference implementations, not curve-fit production systems — their PnL is regime-dependent, especially on the demo synthetic dataset. Pair with real broker tick data through the same parquet pipeline to evaluate seriously.

### Synthesise a dataset and run a backtest

```powershell
dotnet build src\TradingTerminal.Backtest.Cli

# Generate 10k ticks of a mean-reverting random walk with variable L1 sizes
.\src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe synth `
    --output bt-data.parquet --ticks 10000

# Run a strategy with maker/taker fees
.\src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe run `
    --strategy avellanedaStoikov --symbol TEST --data bt-data.parquet `
    --tick-size 0.01 --taker-fee 0.01 --maker-rebate 0.005

# Grid-sweep parameters in parallel
.\src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe sweep `
    --strategy meanReversion --symbol TEST --data bt-data.parquet `
    --lookback "50,100,200" --entry "0.05,0.10,0.20" --stop "0.20,0.40" `
    --output sweep.csv --parallel 8
```

`run` outputs land in `./bt-results/`: `summary.json` (stats + metadata), `trades.csv`, `equity.csv`. `sweep` outputs a single CSV with 15 columns including Sharpe, Sortino, **Calmar, Omega, max-drawdown, Ulcer index, max consecutive losses**, win rate, profit factor, expectancy, fees / rebates, and ending cash.

### Fees, risk, and execution realism

- **`IFeeModel`** (Core/Trading) — three concrete models ship: `ZeroFeeModel` (default), `MakerTakerFeeModel` (per-unit rebates and fees), `BpsFeeModel` (flat bps on notional). The simulated order book tags each fill as `Maker` (limit) or `Taker` (market/stop) via `OrderEvent.Liquidity`, so the right side of the schedule fires automatically. CLI flags: `--taker-fee`, `--maker-rebate`, `--fee-bps`.
- **`IRiskManager`** (Core/Risk) — per-symbol absolute-position cap and per-UTC-day realised-loss cap. Wraps `BacktestOrderRouter`; rejections surface as `OrderState.Rejected` `OrderEvent`s on the strategy's existing event stream. Same accounting will be re-used by the live router when the OMS lands.
- **`Microstructure` helpers** (Core/MarketData) — L1: `Microprice`, `QueueImbalance`, `HalfSpread` (pure functions with `Tick` overloads). L2 (consume `DepthSnapshot`): `CumulativeImbalance`, `WeightedMidPrice`, `SideDepth`, `EstimatedSlippage(side, qty, out fullyFilled)`, `LargestLevelGap`. Plus `Indicators.{SimpleMovingAverage, RollingStdev, ExponentialMovingAverage, RelativeStrengthIndex, AverageTrueRange}` streaming primitives shared by the strategy library.
- **L2 / depth-of-market** (Core/Domain) — `DepthLevel(Price, Size)` and `DepthSnapshot(TimestampUtc, Bids, Asks)` records flow through `IBrokerClient.SubscribeDepthAsync` and the repository (UI-marshalled). Wired in `RealCTraderClient` via `ProtoOASubscribeDepthQuotesReq` / `ProtoOADepthEvent` (incremental new/deleted quotes → reconstructed snapshots). IB and NT throw `NotSupportedException` — IB has a `reqMktDepth` path that's pending callback plumbing; NT's `NTDirect` doesn't expose L2 at all.

### Architecture

- `Core/Backtest/IBacktestStrategy` — implementation gets `OnStart`/`OnTick`/`OnOrderEvent`/`OnEnd` callbacks and an `IOrderRouter` for fills.
- `Infrastructure/Backtest/BacktestSession` — orchestrates the replay loop, advances `SimulatedClock`, evaluates `SimulatedOrderBook` against each tick, runs the optional `IRiskManager` before submission, and tracks P&L through `TradeLedger` (which also deducts fees per fill).
- `Infrastructure/Backtest/L1FillModel` — market orders cross the spread plus `slippageTicks × tickSize`; limit/stop fill when the relevant touch crosses the level. Limits are tagged as Maker; market/stop as Taker.
- `Infrastructure/Backtest/Persistence` — streaming `ParquetTickReader`/`Writer`, row-group buffered (default 50k rows).
- `Infrastructure/Backtest/StatisticsCalculator` — Sharpe/Sortino annualised from the median equity-sample gap; plus Calmar (annualised CAGR / MDD), Omega (Σ gains / Σ losses), Ulcer index (RMS of pct drawdowns), recovery factor, downside deviation, max consecutive losses.

Adding a backtest strategy = one class implementing `IBacktestStrategy` + one entry in `BacktestStrategyCatalog` (UI) / `ResolveStrategy` (CLI). The shared `Indicators` and `Microstructure` modules cover most building blocks — there's rarely a reason to roll your own SMA/EMA/RSI.

## Adding a new strategy

Each strategy is its own project (`src/TradingTerminal.Strategies.<Name>/`) following the
same 6-file shape as RSI / Cumulative Delta and every other strategy on disk. The fastest
path is to copy an existing project, rename, and edit:

1. Copy the closest existing project under `src/`. Rename the directory, the `.csproj`,
   and the per-strategy class prefix (e.g. `BollingerStrategy*` → `MyStrategy*`).
2. Files in the new project:
   - `MyStrategy.cs` — `ITradingStrategy` descriptor with `Id`/`DisplayName`/`Description`.
   - `MyStrategyViewModel.cs` — extends `LiveSignalStrategyViewModelBase` (in
     `TradingTerminal.UI`). Declare your parameters as `[ObservableProperty]`s, override
     `BuildStrategy(Contract contract)` to return your engine-side `IBacktestStrategy` impl.
   - `MyStrategyWindow.xaml`(`.cs`) — a `MetroWindow`. Surface parameter inputs, controls,
     charts however you want.
   - `DependencyInjection.cs` — one `AddMyStrategy()` extension that registers the
     descriptor, VM, view, and a `StrategyFactoryRegistration`.
3. Add a `<ProjectReference>` to your new project in `TradingTerminal.App.csproj`.
4. Add `services.AddMyStrategy();` to `AppDependencyInjection.AddStrategyPlugins()`.
5. Add the project entry to `TradingTerminal.sln`.

The new strategy shows up in the left pane on next launch and opens as its own window.

### Generator for the boilerplate

If you're scaffolding many strategies at once, edit the manifest in
`scripts/gen-strategy-projects.ps1` and re-run it. The script overwrites the generated
files in each project, so customise *new* files in those projects (or fork the generator)
rather than editing the ones it produces.

## Adding a new broker

1. Add a `BrokerKind` enum value in `Core/Brokers/BrokerKind.cs`.
2. Add `XxxOptions` in `Core/Configuration/`.
3. Implement `IBrokerClient` (`Core/MarketData/IBrokerClient.cs`) in `Infrastructure/Xxx/`. Provide both:
   - `RealXxxClient` — the actual integration. Gate behind a compile-time constant if it depends on a sideloaded DLL (mirror the `HAS_IBAPI` / `HAS_NTAPI` pattern in `Infrastructure.csproj`).
   - `FakeXxxClient` — a synthetic fallback so the build is always green.
4. Register both `IBrokerClient` and `BrokerConnectionMode` for the new broker in `DependencyInjection.cs`. The `BrokerSelector` auto-discovers them via `IEnumerable<IBrokerClient>`.
5. Add a tile + form panel to `LoginWindow.xaml` (alongside the existing IB / NT / cTrader / Alpaca tiles) and a corresponding `SelectXxx` command + form fields to `LoginViewModel`.

The `MarketDataRepository`, `ConnectionManager`, all view-models, and every strategy stay untouched — they talk to `IBrokerClient` exclusively.

## Solution layout

```
TradingTerminal/
├── src/
│   ├── TradingTerminal.App                       WPF entry, DI bootstrap, MainWindow, LoginWindow
│   │   ├── Backtest/                             Backtest tab view + view-model
│   │   ├── BrokerForms/                          AlpacaLoginForm (XAML + VM)
│   │   ├── Composition/                          Per-feature DI extensions (manifest for App.OnStartup)
│   │   ├── Login/ Login/Forms/                   Login window + per-broker form view-models (IB, NT, cTrader)
│   │   ├── Notifications/                        Settings tab VM/View + per-user notifications.json writer
│   │   ├── Recording/                            Live tick recorder tab
│   │   ├── Research/                             Factor research tab
│   │   ├── Shell/                                DockTab POCO, MainShellFactory, LoginShellFactory
│   │   └── Strategies/                           StrategyFactory (DI-backed)
│   ├── TradingTerminal.Backtest.Cli              Headless backtest runner (daxalgo-backtest exe)
│   ├── TradingTerminal.Core                      Domain models + interfaces (no UI/broker deps)
│   │   ├── Backtest/                             IBacktestStrategy, IBacktestSession, BacktestConfig/Result/Stats (+Calmar/Omega/Ulcer/…),
│   │   │                                          Trade, EquityPoint, FillRecord, TransactionCostAnalysis, MonteCarlo, BacktestStrategyOption
│   │   ├── Brokers/                              BrokerKind, IBrokerSelector, BrokerConnectionMode
│   │   ├── MarketData/                           IBrokerClient, IMarketDataRepository, Microstructure (L1+L2), Indicators (SMA/EMA/RSI/ATR/stdev)
│   │   ├── Ml/                                   TripleBarrierLabeler, OnlineLinearRegression, FactorComputation
│   │   ├── Domain/                               Bar, Tick, Contract, BarSize, ConnectionState, DepthLevel, DepthSnapshot
│   │   ├── Notifications/                        StrategyNotification, INotificationPublisher, INotificationTransport, INotificationEnricher
│   │   ├── Risk/                                 IRiskManager, RiskManager, RiskOptions
│   │   ├── Strategies/                           ITradingStrategy, IStrategyFactory, StrategyHost
│   │   ├── Time/                                 IClock
│   │   ├── Trading/                              IOrderRouter, OrderRequest/Result/Event (+LiquidityFlag), IFeeModel (zero/maker-taker/bps)
│   │   ├── Configuration/                        InteractiveBrokers/NinjaTrader/CTrader options
│   │   ├── Events/                               IEventBus, EventBus
│   │   └── Session/                              SessionContext
│   ├── TradingTerminal.Infrastructure
│   │   ├── Backtest/                             Engine — SimulatedClock, L1FillModel, SimulatedOrderBook (tags Maker/Taker),
│   │   │                                          BacktestOrderRouter (risk-aware), BacktestSession, TradeLedger (fee-aware),
│   │   │                                          StatisticsCalculator, Persistence/ParquetTick{Reader,Writer},
│   │   │                                          IBacktestStrategyRegistry, BacktestStrategyCatalog,
│   │   │                                          Strategies/ (24 IBacktestStrategy implementations — engine-side logic;
│   │   │                                          the live wrappers live in TradingTerminal.Strategies.<Name>/)
│   │   ├── Brokers/                              BrokerSelector
│   │   ├── Ib/                                   RealIbClient (#if HAS_IBAPI), ConnectionManager
│   │   ├── NinjaTrader/                          RealNinjaClient (#if HAS_NTAPI)
│   │   ├── CTrader/                              RealCTraderClient (live spot + L2 depth)
│   │   ├── Alpaca/                               RealAlpacaClient (REST history + WS ticks, stocks + crypto)
│   │   ├── MarketData/                           MarketDataRepository
│   │   ├── Notifications/                        Dispatcher (channel + hosted worker + enricher pipeline),
│   │   │                                          Telegram + Discord transports, Ollama commentary enricher
│   │   ├── Time/                                 SystemClock
│   │   ├── Trading/                              LiveOrderRouter (delegates to active IBrokerClient)
│   │   └── Threading/                            IUiDispatcher, WpfDispatcher
│   ├── TradingTerminal.UI                        ViewModelBase, dark theme, log sink, DockTabStyleSelector, TaskExtensions,
│   │                                              shared live-signal plumbing: SignalGeneratorRouter, ISignalGeneratorRouterFactory,
│   │                                              SignalEntry, TradeableInstrument + SignalInstrumentCatalog,
│   │                                              LiveSignalStrategyViewModelBase (base class for the per-strategy VMs)
│   ├── TradingTerminal.Strategies.Rsi              RSI Overbought/Oversold strategy
│   ├── TradingTerminal.Strategies.CumulativeDelta  Cumulative Delta Scalper (sniper-mode, 5-confirmation gate)
│   ├── TradingTerminal.Strategies.Microprice       Microprice deviation (HFT)
│   ├── TradingTerminal.Strategies.OrnsteinUhlenbeck OU mean reversion (HFT)
│   ├── TradingTerminal.Strategies.AvellanedaStoikov Avellaneda-Stoikov market maker (HFT)
│   ├── TradingTerminal.Strategies.Twap             TWAP buy execution (HFT)
│   ├── TradingTerminal.Strategies.Bollinger        Bollinger band reversion (Forex)
│   ├── TradingTerminal.Strategies.MaCrossover      Golden / death cross (Forex)
│   ├── TradingTerminal.Strategies.ConnorsRsi2      Connors RSI(2) reversion (Forex)
│   ├── TradingTerminal.Strategies.LondonOpenBreakout London-open breakout (Forex)
│   ├── TradingTerminal.Strategies.Macd             MACD signal crossover (Forex)
│   ├── TradingTerminal.Strategies.TrendFilter      200-SMA trend filter (Index)
│   ├── TradingTerminal.Strategies.VolatilityTargeted Volatility targeting (Index)
│   ├── TradingTerminal.Strategies.GapFade          Overnight gap fade (Index)
│   ├── TradingTerminal.Strategies.EodMomentum      End-of-day momentum (Index)
│   ├── TradingTerminal.Strategies.PullbackContinuation Trend pullback continuation (Index)
│   ├── TradingTerminal.Strategies.BookPressure     Cumulative imbalance (L2)
│   ├── TradingTerminal.Strategies.LiquiditySweep   Sweep / aggressive-flow detector (L2)
│   ├── TradingTerminal.Strategies.IcebergDetection Sticky-touch iceberg heuristic (L2)
│   ├── TradingTerminal.Strategies.OrderFlowToxicity VPIN-style toxicity (L2)
│   ├── TradingTerminal.Strategies.ThinBookFilter   Thin-book breakout filter (L2)
│   ├── TradingTerminal.Strategies.OnlineRegressionAlpha RLS-fit alpha (ML)
│   └── TradingTerminal.Strategies.AnomalyDetector  Rolling z-score anomaly detector (ML)
├── scripts/
│   └── gen-strategy-projects.ps1                 Boilerplate generator for the per-strategy projects above
├── docs/
│   ├── architecture.md                           Design rationale, key interfaces, dep graph
│   └── user-guide.md                             End-user manual (login, strategies, notifications, backtest, etc.)
└── tests/
    └── TradingTerminal.Tests                     xUnit + FluentAssertions + NSubstitute (59 tests across engine, ML, microstructure)
```

## Tests

```powershell
dotnet test
```

Coverage (31+ tests, growing):
- **Strategy factory** registers and resolves strategies + sets `DataContext`, throws on unknown ids.
- **Connection manager** reconnects after the underlying client drops, with backoff, and re-wires when the active broker changes.
- **Backtest engine** — buy-then-sell across a synthetic tick window produces one trade with the expected price/PnL; parquet tick round-trips preserve byte-for-byte content; stats calculator returns expected Sharpe / Sortino / drawdown on a known curve.
- **Fee model** — Maker/Taker per-unit and Bps-on-notional charge correctly; taker fees reduce ending cash and surface on `BacktestResult.TotalFees`.
- **Risk manager** — per-symbol cap rejects accumulating positions, daily loss cap rejects after threshold and resets at UTC midnight, fills are idempotent.
- **Microstructure helpers** — L1: microprice leans toward the thinner side, falls back to mid when sizes are zero; queue imbalance is bounded and signed. L2: `CumulativeImbalance` saturates with heavy bid/ask books, `WeightedMidPrice` pulls toward the heavier side, `EstimatedSlippage` walks levels (correct avg-fill and insufficient-liquidity flag), `LargestLevelGap` picks the biggest step.
- **Indicators** — SMA/EMA/Wilder-RSI/ATR/rolling-stdev math (Bessel correction, RSI saturation in both directions, EMA recursion).
- **MarketDataRepository.SubscribeBarsAsync** propagates the underlying client's "not connected" error through the broker selector.

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
| Alpaca login fails immediately (`auth failed`) | API key id or secret is wrong, or the key was minted on a different environment (paper key against the live endpoint, or vice versa). Re-check the paper / live toggle and regenerate the key from the dashboard if needed. |
| Alpaca: `NotSupportedException` on subscribe | The contract's `SecType` isn't `STK` or `CRYPTO`. Alpaca options aren't wired yet — route options through IB. |
| Alpaca: `NotSupportedException` from `SubscribeDepthAsync` | Expected — Alpaca only exposes L1 quotes. Route depth-of-market subscriptions through IB or cTrader. |
| Alpaca: stock `sip` feed returns no data | The `sip` consolidated feed needs a paid subscription on the Alpaca account. Switch the feed dropdown back to `iex` (free). |
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
