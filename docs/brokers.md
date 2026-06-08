# Broker setup

> Last updated: 2026-06-08

The terminal speaks to four real broker backends behind one `IBrokerClient` seam: **Interactive Brokers** (TWS API), **NinjaTrader 8** (`NTDirect.dll` P/Invoke), **cTrader** (Spotware Open API 2.0 over TLS+protobuf), and **Alpaca** (REST + WebSocket via `Alpaca.Markets`) — plus an in-process **`Simulated`** backend for fully-offline development (see [below](#simulated-offline-development)).

This doc covers how to set each one up. For the architectural rationale and per-broker quirks (callback shapes, threading, depth-of-market support), read [architecture.md](architecture.md). For symptoms / fixes when something goes wrong, see [troubleshooting.md](troubleshooting.md).

## Screenshots

![Login screen](../images/loginscreenwindow.png)

| Interactive Brokers | cTrader | Alpaca |
|---|---|---|
| ![IB login](../images/inteactivebrokerloginwindow.png) | ![cTrader login](../images/ctraderloginwindow.png) | ![Alpaca login](../images/alpacaloginwindow.png) |

## Capability matrix

| Broker | Transport | Real client status | Historical | Live ticks | L2 depth | Order routing |
|---|---|---|---|---|---|---|
| Interactive Brokers | TCP socket → `EClientSocket` | Wired when `CSharpAPI.dll` is found at build time | Real (`reqHistoricalData`) | Real (`reqMktData` L1) | `reqMktDepth` exists — not yet wired, throws | Not yet wired |
| NinjaTrader 8 | `NTDirect.dll` P/Invoke (ANSI) | Wired when `NTDirect.dll` is found at build time | Synthesized (NTDirect has no historical export) | Real, polled at 200 ms | Not exposed by NTDirect — out of scope | Real, via `NTDirect.Command(...)` |
| cTrader | TLS + protobuf to Spotware cloud | Always wired (NuGet package always restores) | Real (`ProtoOAGetTrendbarsReq`) | Real, push (`ProtoOASpotEvent`) | Real, push (`ProtoOASubscribeDepthQuotesReq`) | Real, via `ProtoOANewOrderReq` |
| Alpaca | REST (history) + WebSocket (live) | Always wired (NuGet package always restores) | Real (`HistoricalBarsRequest` / `HistoricalCryptoBarsRequest`) | Real, push (`IAlpacaDataStreamingClient`) | Not exposed by Alpaca — throws | Not yet wired |
| Simulated | In-process, no SDK, no network | Always registered | Replay from local store | Synthetic random-walk **or** store replay | Supported (synthetic + replay) | n/a (data/signals only) |

There are **no per-broker synthetic fallbacks** — each real client is registered only when its SDK is available (IB/NT gated on a resolved DLL; cTrader/Alpaca always restore from NuGet), and a connect simply fails if the broker isn't reachable. To run with no broker at all, use the always-registered **`Simulated`** backend instead (see [below](#simulated-offline-development)).

## Interactive Brokers

### Prerequisites

- TWS or IB Gateway installed and signed in (paper or live).
- The TWS API package installed. The standard installer drops `CSharpAPI.dll` at `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\` and the build auto-discovers it from there.

### DLL resolution order

The `Infrastructure` csproj searches, in order:

1. `lib/CSharpAPI.dll` (or `lib/IBApi.dll` for older copies) at the repo root.
2. `$(TwsApiClientDll)` MSBuild property — `dotnet build -p:TwsApiClientDll="D:\path\CSharpAPI.dll"`.
3. `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll` — the standard installer location.

If any resolves, the build prints `IB CSharpAPI resolved from: <path>` and `RealIbClient` is compiled in (`HAS_IBAPI`). Otherwise IB simply isn't registered — there's no synthetic IB fallback; use the `Simulated` backend for an offline feed.

### TWS configuration

In **TWS → File → Global Configuration → API → Settings**:

- Enable ActiveX and Socket Clients.
- Read-Only API (recommended; the included strategies are read-only).
- Socket port: 7497 (TWS Paper) / 7496 (TWS Live) / 4002 (Gateway Paper) / 4001 (Gateway Live).
- Trusted IPs: add `127.0.0.1`.

### `appsettings.json` keys

```json
"InteractiveBrokers": {
  "Host": "127.0.0.1",
  "Port": 7497,
  "ClientId": 1,
  "AccountType": "Paper",
  "MarketDataType": 1
}
```

> The real IB client is wired purely at build time by DLL resolution (`HAS_IBAPI`); there's no longer a `UseRealClient` switch. To run without IB, use the `Simulated` backend (see [below](#simulated-offline-development)). The legacy `UseRealClient` key still present in `appsettings.json` is ignored.

`MarketDataType` accepts `1` (Live), `3` (Delayed, free, ~15 min lag), `4` (Delayed-Frozen). Switch to `3` if you see IB error 10089 ("requires additional subscription").

`ClientId` must be unique across every client connected to the same TWS (Excel sheets, Bookmap, another terminal instance). Pick something unlikely to collide if you run multiple clients.

### 2FA

TWS handles 2FA itself at sign-in time. The API socket has no separate 2FA step — once TWS is signed in, the terminal can connect freely. **Do not** wire 2FA into the terminal's login form.

## NinjaTrader 8

### Prerequisites

- NinjaTrader 8 installed and running.
- **Tools → Options → AT Interface → AT Interface enabled** ticked.
- `NTDirect.dll` available. The standard install puts it at `%USERPROFILE%\Documents\NinjaTrader 8\bin64\NTDirect.dll`.

### DLL resolution order

1. `lib/NTDirect.dll` at the repo root.
2. `$(NinjaTraderApiDll)` MSBuild property.
3. `%USERPROFILE%\Documents\NinjaTrader 8\bin64\NTDirect.dll`.

If any resolves, the build prints `NTDirect resolved from: <path>` and copies the DLL next to the output assembly so P/Invoke finds it.

### `appsettings.json` keys

```json
"NinjaTrader": {
  "AccountName": "Sim101",
  "DefaultFuturesContractMonth": "06-26"
}
```

`AccountName` is the NT account the client drives (default sim is `Sim101`). `DefaultFuturesContractMonth` is appended to bare futures symbols, e.g. `ES` becomes `ES 06-26`. Like IB, the NT client is wired purely by DLL resolution at build time (`HAS_NTAPI`) — the legacy `UseRealClient` key is no longer read.

### Known limitations

- **No historical bar API in NTDirect.** `RequestHistoricalBarsAsync` synthesizes a series anchored on the current `LastPrice` so charts have a baseline.
- **No L1 sizes via `Bid`/`Ask`.** `Tick.BidSize` and `Tick.AskSize` always come back as 0.
- **No L2.** NinjaTrader's depth-of-market lives behind NinjaScript SuperDOM, which isn't reachable from the AT Interface.

## cTrader

### Prerequisites

- A cTrader-compatible broker account (FXCM, Pepperstone, IC Markets, etc.).
- An OAuth app registered at [connect.spotware.com/apps](https://connect.spotware.com/apps).

### One-time OAuth setup

1. Register an app at [connect.spotware.com/apps](https://connect.spotware.com/apps). Note the **Client ID** and **Client Secret**.
2. Run the OAuth flow ([Spotware docs](https://help.ctrader.com/open-api/account-authentication/)) to get an **access token** for your trading account.
3. Find your **ctidTraderAccountId** by sending `ProtoOAGetAccountListByAccessTokenReq` with the access token (or check the Spotware portal).
4. Paste the four values into the cTrader form on the login screen.

### `appsettings.json` keys

```json
"CTrader": {
  "Host": "demo.ctraderapi.com",
  "Port": 5035,
  "IsLive": false
}
```

The credentials themselves are not in `appsettings.json` — they are entered on the login form and stored DPAPI-encrypted at `%LOCALAPPDATA%\DaxAlgoTerminal\connection.json`.

### Token expiry

Access tokens expire after ~30 days. The first sign that this has happened is a `ProtoOAErrorRes` immediately on connect — re-run the OAuth refresh and paste the new token into the login form.

## Alpaca

### Prerequisites

- An Alpaca account (paper is free at [app.alpaca.markets](https://app.alpaca.markets); live needs a funded account at [/live](https://app.alpaca.markets/live)).

### Minting the API key

- Paper: dashboard → *Paper trading → API keys → Generate*. Key id starts with `PK…`.
- Live: dashboard → *API keys → Generate*. Key id starts with `AK…`.

Paste both into the Alpaca tile on the login screen. Tick **Use live endpoint** for the funded environment; leave unticked for paper. Pick the stock data feed (`iex` is free; `sip` requires a paid market-data subscription).

### `appsettings.json` keys

```json
"Alpaca": {
  "ApiKey": "",
  "ApiSecret": "",
  "IsLive": false,
  "StockDataFeed": "iex"
}
```

`ApiSecret` is DPAPI-encrypted on disk — the login form is the normal entry path; the `appsettings.json` value is only there as a fallback for headless setups.

### Asset-class routing

`Contract.SecType` drives routing inside `RealAlpacaClient`:

- `STK` / `STOCK` / `EQUITY` → stock REST + streaming clients.
- `CRYPTO` / `CRYPTOCURRENCY` → crypto REST + streaming clients.
- Anything else → `NotSupportedException`. Alpaca options are not yet wired (the SDK's options surface is still stabilising); route options through IB.

### Limitations

- **No L2 depth.** Alpaca only exposes L1 NBBO quotes; `SubscribeDepthAsync` throws `NotSupportedException`. Use IB (when wired) or cTrader for L2.
- **Credentials are mandatory.** Like every real broker, Alpaca has no synthetic fallback — to run without credentials, use the `Simulated` backend below.

## Simulated (offline development)

`BrokerKind.Simulated` is an in-process `IBrokerClient` (`SimulatedBrokerClient`, in `Infrastructure/Simulation/`) with **no broker and no network** — it's always registered, alongside the four real backends. It exists so the whole app (ingest → hub → strategies/tools) can run fully offline. Unlike the real backends it supports **both trade tape and L2 depth** in either mode.

It's not shown as a login tile; it's wired up by the dev launch profiles (`DOTNET_ENVIRONMENT` = `DevSim` / `DevReplay`), which skip login and auto-connect it. Two feed modes, set by `SimulatedBroker:Mode`:

| Mode | What it does |
|---|---|
| `Synthetic` (default) | Deterministic seeded random-walk generated in-process. Needs no recorded data. Lists the instruments in `SimulatedBroker:Instruments`. |
| `Replay` | Streams recorded data out of the local market-data store on a speed-scaled clock (`SpeedMultiplier`), re-emitting it as if live. Lists whatever the store holds; falls back to the synthetic feed for any instrument/stream with no data, so a window is never empty. |

See [configuration.md](configuration.md#simulatedbroker) for the full key reference and [getting-started.md](getting-started.md#dev-launch-profiles-skip-login-run-offline) for the launch profiles.

## Secrets and persistence

| Secret | Where it lives |
|---|---|
| IB password | DPAPI-encrypted in `%LOCALAPPDATA%\DaxAlgoTerminal\connection.json`. |
| cTrader OAuth secret + access token | Same file. |
| Alpaca API secret | Same file. |
| AI Analyst provider API key | DPAPI-encrypted in `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` (different folder — see [ai-analyst.md](ai-analyst.md)). |
| Notification tokens (Telegram bot, Discord webhook URL) | Plain text in `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`. These are low-trust secrets — the bot can only post to one chat, the webhook to one channel. |

DPAPI scope is `DataProtectionScope.CurrentUser` — secrets are only readable by the same Windows user on the same machine. Copying the file to another user / machine renders the ciphertext unusable.
