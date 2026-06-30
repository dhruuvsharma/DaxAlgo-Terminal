# Interactive Brokers TWS — Setup, Precautions & News Configuration

This is the authoritative configuration guide for connecting DaxAlgo Terminal to Interactive
Brokers' Trader Workstation (TWS) or IB Gateway. It covers the API socket settings, the
operational precautions you need to know before flipping to live trading, and how to wire up
news feeds so the AI Analyst pipeline has something to enrich notifications with.

If you're just trying to make the dropdown show more instruments, skip to
[Instrument coverage](#instrument-coverage) at the bottom.

---

## 1. Pre-flight checklist

- TWS or IB Gateway is installed and your account is logged in.
- TWS is the desktop GUI; IB Gateway is the headless variant (less RAM, no charts). Either
  works — the API surface is identical.
- You have at least one **market data subscription** that covers the instruments you intend to
  trade or stream. The free "delayed" tier blocks `reqTickByTickData` (the trade-tape feed) —
  any strategy that needs trade prints (Order Flow Cube / Surface Spike / Imbalance Heat Front)
  will see an empty stream until you upgrade.

## 2. API socket — enable & configure

Open TWS → **File → Global Configuration → API → Settings** (Gateway: **Configure → Settings → API → Settings**).

| Setting                                           | Recommended value                | Why                                                                 |
| ------------------------------------------------- | -------------------------------- | ------------------------------------------------------------------- |
| **Enable ActiveX and Socket Clients**             | ☑ checked                        | Without this the API socket is offline; you get error 502.          |
| **Read-Only API**                                 | ☑ checked (paper) / ☐ (live OMS) | Paper accounts: keep read-only on for safety. Live trading: off.    |
| **Socket port**                                   | `7497` (Paper) / `7496` (Live)   | DaxAlgo's appsettings.json defaults match these.                    |
| **Master API client ID**                          | leave blank                      | "Master" gives one ID privileged access to all orders — not needed. |
| **Allow connections from localhost only**         | ☑ checked                        | Don't accept remote connections unless you've locked down the host. |
| **Trusted IPs**                                   | `127.0.0.1`                      | Only the local machine. Add others only if Gateway runs remotely.   |
| **Bypass Order Precautions for API Orders**       | ☐ unchecked                      | Keep TWS's safety prompts on the OMS path. See §4.                  |
| **Create API order log when API placing orders**  | ☑ checked                        | Auditable trail every time the app submits an order.                |
| **Include FX positions when sending portfolio**   | ☑ checked                        | Needed for accurate exposure tracking.                              |
| **Send status updates for EFP options**           | ☐ unchecked (unless trading EFP) | Reduces noise on `tickPrice` callbacks.                             |
| **Use Account Groups with Allocation Methods**    | ☐ unchecked (single account)     | Only relevant for advisors with sub-accounts.                       |
| **Download open orders on connection**            | ☑ checked                        | App can reconcile its OMS view on reconnect.                        |

### Client ID — critical

DaxAlgo Terminal reads `InteractiveBrokers:ClientId` from `appsettings.json` (default `1`).
**Every concurrent API client must use a unique ClientId**:

- Excel RTD → typically ClientId 0 (don't use 0 elsewhere).
- Bookmap / Quantower / Sierra Chart → check their settings; many default to 1, 2, 3.
- A second DaxAlgo instance on the same TWS → bump the second one's ClientId.

Collisions surface as **error 326**. The first connection wins; the second is refused with a
clear message in the Activity log drawer.

### Auto-restart (sane default)

TWS asks you to re-authenticate every ~24 hours, which kills the API socket. To avoid this:

1. **File → Global Configuration → Lock and Exit → Set Auto Logoff Time → 11:55 PM**
2. **File → Global Configuration → Auto Logoff Time → toggle "Auto restart"** — TWS restarts
   itself without asking for 2FA for 7 days at a stretch.

After 7 days you must log in manually once (this is IB's policy, not configurable). For 24/7
unattended operation use IB Gateway with the `ibc` wrapper script.

## 3. Connection sanity tests

After flipping the API toggle, confirm DaxAlgo can reach TWS:

1. Start TWS / Gateway.
2. Start DaxAlgo. Open the Activity log drawer (bottom of the main window).
3. Pick the IB tab in the login window, leave defaults, click Connect.

Expected log lines (in order):
```
IB CSharpAPI resolved from: C:\TWS API\...
Connecting to IB at 127.0.0.1:7497 with clientId 1
IB ManagedAccounts received: DU1234567
IB nextValidId received: 12345
Instrument discovery starting for InteractiveBrokers
Instrument discovery complete for InteractiveBrokers: 400/400 contracts registered
```

If you see **error 502** instead → TWS is not running, or the port is wrong, or a firewall
is blocking 7497/7496.

If you see **error 326** → ClientId collision with another tool. Change
`InteractiveBrokers:ClientId` in `appsettings.local.json` and restart DaxAlgo.

If you see **delayed market-data** banners → see §5.

## 4. Live-trading precautions (read before flipping `IsPaper` off)

This section is not optional. Live orders execute in milliseconds and a misconfigured strategy
can drain an account before you can blink.

### Order-precaution settings (TWS side)

**File → Global Configuration → Presets → Stocks / Futures / FX / Options →** for every
instrument class you intend to trade live:

- **Size Limit** — hard cap per order. Pick a number that's 2× your largest intended trade
  size. Anything bigger triggers a TWS confirmation prompt that the API can't bypass unless
  you checked "Bypass Order Precautions for API Orders" (you didn't — see §2).
- **Total Value Limit** — hard cap on notional ($) per order. Same logic: 2× your max
  intended notional.
- **Percentage of Average Daily Volume** — TWS warns when a single order would exceed N% of
  ADV (default 1%). Leave this on.
- **Tick / Pip Limit (Marketable Orders)** — TWS warns when a market order would walk the
  book more than N ticks. Leave this on; it catches fat-finger limits sent as market.

### Account-side precautions

**Account → Account Management → Trading → Pre-Trade Risk Controls**:

- **Maximum order size** — fill in for every product type. Account-level cap that wins over
  TWS-level Presets if the two disagree.
- **Maximum daily loss** — the OMS pulls the plug for the day if cumulative P&L hits this.
  Set it to a number that hurts but doesn't kill you.
- **Pattern Day Trader (PDT) protection** — if your account is below $25k and US equities,
  this prevents the 4th day-trade rolling 5 trading days. Keep enabled.

### DaxAlgo-side precautions

- `LiveOrderRouter` is **not wired today**. Every strategy that emits a signal goes through
  the simulated `BacktestOrderRouter` for visualization only; no real broker order is placed.
  If you wire `LiveOrderRouter`, double-check the `IFeeModel` and `IRiskManager` in DI before
  you flip the toggle.
- The login form's `IsPaper` checkbox flips between port 7497 (paper) and 7496 (live). It
  does **not** modify any risk limit — those come from TWS/account settings above.
- The `Risk → Stop Trading` button in MainWindow is wired to disarm every active strategy's
  algo (it does NOT cancel resting orders — there are none today, but plan accordingly).
- Notification fanout (Telegram / Discord) reports every signal. If you don't want your
  group seeing live trades, mute the relevant transport in **Tools → Notifications**.

## 5. Market data tier — live vs delayed

DaxAlgo sets `InteractiveBrokers:MarketDataType` (in `appsettings.json`) to:

- `1` = Live (default). Requires an active IB market-data subscription for the exchange of
  the contract you're subscribing to. Quotes are real-time; `reqTickByTickData` works.
- `2` = Frozen (last good quote at session close). Useful after-hours for diagnostics only.
- `3` = Delayed (15-min lag, free with any account). Quotes work; **trade tape doesn't**.
  `reqTickByTickData` returns error 10189 — any strategy that calls `OnTradeAsync` will see
  an empty stream forever.
- `4` = Delayed-Frozen.

If your strategy log shows `IB market-data: Delayed` and you wanted live:

1. Verify you actually have a paid market-data subscription in **Account → Subscriptions**.
2. Set `InteractiveBrokers:MarketDataType: 1` in `appsettings.local.json`.
3. Reconnect.

The strategy windows that consume trade tape will print a clear WARN on the log panel when
they detect Delayed mode — don't ignore it.

## 6. News configuration

IB exposes news through several services. DaxAlgo's AI Analyst pipeline can consume them via
`reqNewsArticle` / `reqHistoricalNews` (planned — not yet wired in v1). When wiring is added,
the news bulletin types you need are:

### Step 1 — subscribe to news providers

**Account → Subscriptions → Research Subscriptions**:

| Provider                                | Code     | Cost (typical)        | Coverage                              |
| --------------------------------------- | -------- | --------------------- | ------------------------------------- |
| **Briefing.com General Market Briefing**| `BRFG`   | Free                  | US headlines, calendar, earnings.     |
| **Briefing.com Trader**                 | `BRFUPDN`| ~$50/mo               | Active-trader feed, FOMC, rate-call.  |
| **Dow Jones News Service**              | `DJNL`   | ~$10/mo               | Dow Jones wire, basic.                |
| **Dow Jones Real-Time News**            | `DJ-RT`  | ~$40-100/mo           | Full Dow Jones, including pro feeds.  |
| **Reuters Basic / StreetEvents**        | `RSF`    | ~$50/mo               | Reuters general newswire.             |
| **Reuters StreetEvents Calendar**       | `RTRS`   | ~$25/mo               | Earnings calendar, conference dates.  |
| **Hammerstone Markets News**            | `HM`     | ~$30/mo               | Equity flow / unusual options.        |
| **IBKR News (Free)**                    | (none)   | Free                  | IBKR-curated headlines, no API push.  |

The free **IBKR News** feed is visible in the TWS News window but is **not** available via the
API — that's an IB limitation, not a DaxAlgo one. To get news into DaxAlgo programmatically
you need at least one paid feed (BRFG-Trader and DJNL are the cheapest entry points).

### Step 2 — confirm news codes in TWS

After subscribing, TWS → **News → Market Bulletins → Configure → check the providers** you
just subscribed to. Their bulletin codes appear in the dropdown:

- `BRFG` (Briefing.com)
- `BRFUPDN` (Briefing Trader)
- `DJNL` / `DJ-RT` (Dow Jones)
- `RSF` (Reuters)
- `HM` (Hammerstone)

DaxAlgo's planned news ingestor reads `reqNewsProviders()` first, then filters to whichever
codes you've enabled in `appsettings.json` under `InteractiveBrokers:NewsProviders` (planned
key — not yet read).

### Step 3 — news per contract

`reqHistoricalNews(reqId, conId, providerCodes, startTime, endTime, totalResults)` requires:

- **conId** — the IB-assigned contract id (the InstrumentRegistry resolves and caches this).
- **providerCodes** — colon-separated list, e.g. `BRFG+DJNL+RSF`.
- A market-data subscription that covers the contract (you can't fetch news for symbols you
  can't see quotes for).

### Step 4 — news bulletin events (real-time push)

TWS emits push news via the `newsBulletins` EWrapper callback. Three bulletin types:

| Type | Meaning                                                  | Action                                                   |
| ---- | -------------------------------------------------------- | -------------------------------------------------------- |
| 1    | Regular news bulletin                                    | Display in the news panel; AI Analyst enrichment input.  |
| 2    | Exchange not available for trading                       | Disarm strategies trading on that exchange.              |
| 3    | Exchange has become available again                      | Re-arm previously disarmed strategies (optional).        |

Strategies that key off exchange availability (futures-only ones, especially) should hook
type 2/3 bulletins. None do today; it's a planned addition.

---

## Instrument coverage

If you can't find your symbol in the picker dropdown:

- The IB picker now shows ~400 hand-curated instruments (Dow 30, NASDAQ-100, S&P heavyweights,
  every sector / commodity / bond / volatility / leveraged / international ETF family, every
  CME/NYBOT/NYMEX/COMEX futures family, 40+ FX pairs).
- IB has no "list everything" API call — `reqMatchingSymbols` returns at most 16 matches per
  search and isn't suitable for populating a dropdown.
- Symbols **not** in the curated list still resolve on demand: in any strategy window, type
  the ticker in the symbol search box. The dropdown filter searches the AllInstruments cache;
  if your symbol isn't there but TWS knows it, you can subscribe by constructing the contract
  manually — but the picker won't show it. The catalog is a UX-affordance, not an allowlist.
- For US equities not in the catalog, edit
  `src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/IbCuratedCatalog.cs` and add a row to the appropriate
  list (`Sp500Heavyweights()` is the easiest place).

For cTrader, the picker shows **every symbol the connected cTID account has permissions for**.
If you don't see XAUUSD or metals, it's a broker-account limitation — different cTrader
brokers offer different symbol sets. IC Markets, Pepperstone, and FXPro all include metals +
indices + crypto CFDs; Spotware demo accounts are FX-only.

For Alpaca, the picker shows all ~11,000 tradeable US equities + crypto. The dropdown displays
the first 500 alphabetically until you type in the search box — use search to find any symbol
fast (it filters across the full universe before capping).
