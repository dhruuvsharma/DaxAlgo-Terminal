# DaxAlgo Terminal — User Guide

A practical walkthrough for **using** the terminal. Read `README.md` for the engineering
overview and `docs/architecture.md` for the design rationale. This document assumes the
app builds (`dotnet build` in the repo root) — see the README's Prerequisites section if
that's not the case yet.

---

## 1. First launch

```powershell
dotnet run --project src/TradingTerminal.App
```

You'll see the **login window** with four broker tiles: Interactive Brokers, NinjaTrader,
cTrader, Alpaca. Pick one — the terminal connects to whichever broker you log in with,
and swaps the entire data path (history, ticks, depth, connection state) accordingly.

| Broker | Prereq | How to fill in the form |
|---|---|---|
| **Interactive Brokers** | TWS or IB Gateway running and signed in. **API → Settings → Enable ActiveX and Socket Clients** ticked. Trusted IP `127.0.0.1` added. | Host `127.0.0.1`, port 7497 (TWS Paper), 7496 (TWS Live), 4002 (Gateway Paper), or 4001 (Gateway Live). Client ID = any integer not used by another connected client. |
| **NinjaTrader 8** | NT 8 running. **Tools → Options → AT Interface → AT Interface enabled** ticked. | Account name (default `Sim101`). Default contract month (e.g. `06-26`) only matters if you trade bare futures symbols. |
| **cTrader** | OAuth app registered at [connect.spotware.com/apps](https://connect.spotware.com/apps), access token obtained. | Client ID + Client Secret (from your registered app), Access Token (from the OAuth flow), CtidTraderAccountId (numeric, from the account-list endpoint). Tick **Use live endpoint** for production. |
| **Alpaca** | API key id + secret minted from the [Alpaca dashboard](https://app.alpaca.markets) (paper) or [/live](https://app.alpaca.markets/live) (funded). | API key id (starts `PK…` paper / `AK…` live), API secret, stock data feed (`iex` free / `sip` paid). Tick **Use live endpoint** for the funded account; leave unticked for paper. Stocks (`STK`) and crypto (`CRYPTO`) work; options not yet wired; no L2 depth. |

After **Sign in**, the main shell opens. The status bar at the bottom shows connection
state, your user/account, the active broker, and a tab count.

> If the login fails, watch the **Logs** pane at the bottom — every broker error is logged
> there with enough detail to act on (IB error codes, cTrader `ProtoOAErrorRes`, NT
> `rc != 0` reasons).

---

## 2. The main shell

```
+------------------------------------------------------------+
| File   View   Tools   Settings                             |
+---------------+--------------------------------------------+
|  Strategies   |  Documents area                            |
|  - RSI        |   (each opened strategy / tool is a tab    |
|  - CumDelta   |    or window here)                         |
|  - Microprice |                                            |
|  - OU         |                                            |
|  - …          |                                            |
|  - Anomaly    |                                            |
+---------------+--------------------------------------------+
|  Logs                                                      |
+------------------------------------------------------------+
| ●Connected  user · acct   Live cTrader   12:34:56  Tabs: 2 |
+------------------------------------------------------------+
```

- **Left pane (Strategies)**: list of every strategy registered with the app. Double-click
  any entry to open it.
- **Document area (centre)**: tabs for tools (Backtest, Recorder, Factor Research,
  Notifications settings) and inline strategy panes. Strategies usually open as their own
  **floating window**, not as a tab.
- **Logs pane (bottom)**: in-memory Serilog sink. Live tail of everything happening — turn
  on if a strategy isn't behaving as you expect.
- **Status bar (bottom row)**: connection-state dot (green = connected), signed-in user,
  broker mode badge (green = live, yellow = paper/synthetic), wall clock, open-tab count.
- **View menu** toggles the left pane and the logs pane on/off if you want a cleaner
  workspace.

---

## 3. Running a strategy live (signal mode)

Every strategy ships as a separate window. The pattern is identical for all of them:

1. **Double-click** the strategy in the left pane. A `MetroWindow` opens with the
   strategy's title in its title bar.
2. **Pick an instrument** from the dropdown. The catalogue covers common ETFs, big-name
   US stocks, continuous futures, and spot FX. To trade something not on the list, see
   §10 below.
3. **Edit the parameters** in the "Parameters" panel — each strategy exposes the knobs
   from its underlying logic (period, threshold, lookback, etc.) as text fields. Defaults
   are sensible, but they're not optimised for any particular instrument.
4. **Hit Start.** The window subscribes to the live tick stream for the chosen contract
   via the active broker. The stats bar (status / ticks / bid / ask / signals) starts
   updating. Whenever the strategy would submit an order, a row appears in the **signal
   log grid** AND a `StrategyNotification` is published — see §4 for where those go.
5. **Hit Stop** to flatten the strategy's internal state and stop receiving ticks. **Clear
   log** clears the in-window grid (notifications already sent stay sent).

> **No order ever leaves the terminal.** Every strategy runs in "signal mode" — the
> underlying engine produces orders that the host's synthetic router intercepts and
> surfaces as notifications. Execution is delegated to whatever app you actually trade
> from (Bookmap, your broker's own platform, a custom OMS, etc.).

> Closing the window resets the strategy's state. Re-opening it gives you a fresh
> instance. If you want to keep state across sessions, leave the window open.

---

## 4. Notifications (Telegram, Discord, Ollama)

Every signal fired by any strategy goes through a single notification pipeline. **Tools →
Settings → Notifications…** opens the configuration tab.

### Telegram

1. Open Telegram, talk to [@BotFather](https://t.me/BotFather), `/newbot`, follow the
   prompts, copy the token (looks like `123456:AA…`).
2. Get the chat ID: message [@userinfobot](https://t.me/userinfobot) for personal/group
   chats (it'll reply with a numeric id), or use `@channelname` for a public channel.
3. In the Settings tab: tick **Enabled**, paste the bot token, paste the chat ID,
   **Save**. Hit **Send test** — you should get one test message in Telegram within ~1s.

### Discord

1. In your target Discord channel: **Edit Channel → Integrations → Webhooks → New
   Webhook → Copy Webhook URL**.
2. In the Settings tab: tick **Enabled** under the Discord block, paste the webhook URL,
   optionally set a username override, **Save**.

### Local LLM commentary (Ollama)

Adds a one-line plain-English commentary to every notification, generated by a local LLM.
Free, runs entirely on your machine, no API key.

1. Install Ollama from <https://ollama.ai/download>. Run it.
2. Pull a model: `ollama pull llama3.2` (or any model you prefer — `phi3.5`, `mistral`).
3. In the Settings tab: tick **Enabled** under the Ollama block, leave endpoint as
   `http://localhost:11434`, set the **Model tag** to the one you pulled, **Save**.

The LLM is a workflow tool, not a predictive signal. Each call takes 1–4 seconds —
useless for sub-second HFT, but fine for low-frequency signal mode. If Ollama is
unreachable or slow, the enricher times out silently and the notification goes out
unchanged.

### `IncludeIdleSignals`

Both Telegram and Discord blocks have an **Also send idle (unarmed) signals** checkbox.
Off by default — strategies fire "idle" notifications during warmup or sub-armed states;
turning this on means those land in your channels too. Leave off unless you're tuning.

### Persistence

Settings save to `%LocalAppData%\DaxAlgo Terminal\notifications.json`. That file is layered
onto the host config with `reloadOnChange: true`, so edits in the Settings tab take effect
without an app restart.

---

## 4b. AI Market Analyst (AI tools → Market analyst)

A multi-agent LangGraph analyst that runs an indicator → pattern → trend → decision flow
on a chosen symbol/timeframe and returns a structured verdict (`Long` / `Short` / `NoCall`)
with annotated candlestick + trend-channel charts. The reasoning lives in a Python sidecar
(`tools/python-ml/daxalgo-ml.exe`) — the WPF build stays hermetic. Bring your own API key
for OpenAI, Anthropic, Qwen, or MiniMax.

### Setup

1. **Build the sidecar.** From `tools/python-ml/`:
   ```powershell
   python -m venv .venv
   .venv\Scripts\Activate.ps1
   pip install -e .[dev]
   pyinstaller daxalgo_ml.spec
   ```
   That produces `dist/daxalgo-ml.exe`. Copy to `tools/python-ml/bin/daxalgo-ml.exe`.
2. **Run it.** Pick a port and run:
   ```powershell
   $env:DAXALGO_ML_PORT = "8765"
   .\bin\daxalgo-ml.exe
   ```
   The service binds to `127.0.0.1` only.
3. **Wire it up.** In the terminal, **Tools → Settings → Notifications…** scroll to the
   **AI Market Analyst** block. Tick **Enabled**, set the endpoint to
   `http://127.0.0.1:8765`, pick your provider, paste the API key, set the text and
   vision model ids. **Save**.

The API key is stored DPAPI-encrypted under `%LocalAppData%\DaxAlgo Terminal\notifications.json`
(scope: current user, current machine). It is never written in plain text and never appears
in `appsettings.json`.

### Running an analysis

**AI tools → Market analyst** opens the dock pane. Type a symbol, pick a timeframe and
bar count, hit **Analyze**. The terminal pulls bars from the active broker, ships them to
the sidecar, and renders the verdict:

- **Left column** — indicator commentary (RSI/MACD/ATR/EMA panel + plain-English summary).
- **Middle column** — pattern verdict (one of 16 classical patterns: inverse H&S, double
  bottom, wedges, triangles, flags, V-reversal, etc.) plus the rendered candlestick PNG
  the vision LLM scored against.
- **Right column** — trend regime (Up/Down/Flat) plus an annotated chart showing the
  fitted upper/lower linear channel.
- **Bottom strip** — big LONG / SHORT / NO-CALL badge, R:R, forecast horizon,
  justification, confidence.

Every Analyze click runs fresh — there is no cache. The terminal is signal-mode by rule:
the analyst proposes, the human disposes. There is no "place this trade" button on the
pane.

### Per-notification enrichment

Tick **Append the AI Analyst verdict line to every signal notification** in Settings, and
each Telegram / Discord signal carries an extra line like:

> 🤖 AI Analyst: Long (conf 72%, R:R 2.10) — Bull Flag

The enricher runs independently of Ollama; both can be on at once. If the sidecar is down
or the call times out, the original notification goes out unchanged — the AI line is
strictly additive.

### Graceful degradation

With no sidecar running (or **Enabled** off), the AI Analyst pane renders the empty state
cleanly: "AI Analyst unavailable" badge, Analyze button disabled. Nothing in the rest of
the app changes — the existing Ollama enricher and the standard notification pipeline
keep working.

### TROUBLESHOOTING — "Analyst unavailable / Python sidecar not running"

- Hit `http://127.0.0.1:<port>/healthz` directly in a browser. If it doesn't return
  `{"status": "ok"}`, the sidecar isn't running or the port in Settings is wrong.
- Check the sidecar's console for stack traces. Common causes: missing API key,
  invalid model id, missing TA-Lib install (use the Gohlke wheel on Windows), or a
  vision-LLM that rejected the image.
- HTTP 504 from the sidecar means the 60-second top-level timeout tripped — the first
  vision call is often slow; retry once.
- Verify the API key is correct in **Settings → Notifications → AI Analyst**. The key
  field is stored DPAPI-encrypted, so a wrong paste won't ever round-trip; re-paste and
  Save.

---

## 5. Backtesting (Tools → Backtest)

The terminal ships a tick-level backtest engine that runs the same strategies against
historical parquet files.

1. **Generate or obtain tick data.** The simplest path is the CLI's `synth` subcommand
   for a mean-reverting random walk:
   ```powershell
   src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe synth `
       --output bt-data.parquet --ticks 50000 --seed 7
   ```
   For real data, use the **Live tick recorder** (§6 below) on a connected broker.
2. **Open Tools → Backtest**. Pick a strategy from the dropdown, set the symbol, point
   the **Data** path at your parquet file. Configure tick size, slippage, contract
   multiplier, starting cash.
3. Hit **Run**. The engine replays every tick through the strategy and the simulated
   order book. While it runs, the **equity curve** plots live and **trades / stats**
   populate as fills come in.
4. After the run, the results dir (`./bt-results/` by default) contains:
   - `summary.json` — full stats block (Sharpe, Sortino, Calmar, Omega, max drawdown,
     Ulcer index, win rate, profit factor, expectancy, max consecutive losses, total fees,
     ending cash).
   - `trades.csv` — every round-trip with entry/exit timestamps + prices + gross PnL.
   - `equity.csv` — per-sample equity curve.
   - `fills.csv` — per-fill record with simultaneous mid and liquidity flag (Maker/Taker)
     — input for TCA (§7).

---

## 6. Recording live ticks (Tools → Record live ticks)

Build a proprietary tick archive on your account that you can replay through the backtest
engine later.

1. Pick an instrument.
2. Click **Browse…** to pick an output `.parquet` path (default lives in
   `%LocalAppData%\DaxAlgo Terminal\recordings\`).
3. Hit **Start.** The tab streams the live tick feed from the active broker into the
   parquet writer. The grid shows the most recent 30 ticks; the stats row shows
   elapsed time, total ticks written, current bid/ask.
4. **Stop** flushes the writer and closes the file. The resulting parquet is directly
   usable as the `--data` argument to `daxalgo-backtest run` or the Backtest tab.

> L1 only today — depth recording (cTrader's L2) needs a separate columnar format and
> isn't wired yet. Tickers like ES (CME) etc. work; FX through cTrader works.

---

## 7. Transaction-cost analysis (CLI `tca`)

After a backtest run, evaluate whether slippage is killing the strategy:

```powershell
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe tca `
    --results .\bt-results\ `
    [--output tca.json]
```

Console output: TWAP mid, VWAP fill, implementation shortfall (signed; positive = cost
vs TWAP benchmark), mean / VWAP-weighted slippage, slippage P50/P90/P99, maker/taker mix,
and a per-UTC-hour breakdown of fills + mean slippage + maker fraction.

---

## 8. Factor research (Tools → Factor research)

Inspect microstructure features and gauge their predictive shape — the day-to-day quant
researcher loop.

1. **Browse…** for a parquet tick file (synth or recorded).
2. Set **BarTicks** (how many ticks per aggregated bar), **VolWindow** (rolling vol
   estimator window in bars), **ForwardBars** (decile-sort horizon).
3. Hit **Compute**. The tab populates:
   - **Pairwise correlation matrix** between the standard features (`LogReturn`,
     `RollingVol`, `MicropriceDev`, `QueueImbalance`, `Spread`). Look for redundant
     features (|ρ| > 0.7) you should drop.
   - **Decile sort** of the selected feature against forward N-bar returns. A monotone
     shape ⇒ predictive; flat ⇒ no edge at this horizon. Change the **Feature** dropdown
     or **ForwardBars** to re-run instantly.

---

## 9. CLI cheat sheet

The backtest engine is also a headless CLI for scripting / cron / CI. Build it once:

```powershell
dotnet build src\TradingTerminal.Backtest.Cli
```

Then call `daxalgo-backtest.exe <command>`:

| Command | Purpose |
|---|---|
| `synth` | Generate a synthetic mean-reverting parquet tick file. |
| `run` | Single backtest. Emits trades.csv + equity.csv + fills.csv + summary.json. |
| `sweep` | Parameter-grid evaluation in parallel. Emits a CSV with one row per cell. |
| `walkforward` | Rolling train/test windows. Picks best param on train, evaluates OOS on test, emits per-window CSV. |
| `mc` | Bootstrap resample of a trades.csv. Reports distribution stats for Sharpe / MDD / final equity (Bailey-López-de-Prado-style). |
| `tca` | Transaction-cost analysis from fills.csv. |
| `features` | Aggregate ticks → labelled feature CSV ready for any ML library (triple-barrier labelling). |

Run any command with no args to see its specific flags. Example pipeline:

```powershell
$exe = "src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe"

# 1. Build a tape
& $exe synth --output ticks.parquet --ticks 100000

# 2. Pick the best params on a windowed walk-forward
& $exe walkforward --strategy meanReversion --symbol TEST --data ticks.parquet `
                   --windows 5 --train-fraction 0.7 --output wf.csv

# 3. Single run with the best config + maker rebate
& $exe run --strategy meanReversion --symbol TEST --data ticks.parquet `
           --maker-rebate 0.005 --taker-fee 0.01 --output .\final\

# 4. TCA on the run
& $exe tca --results .\final\ --output tca.json

# 5. Monte Carlo on the trade tape
& $exe mc --trades .\final\trades.csv --simulations 10000

# 6. Export labelled features for offline ML training
& $exe features --data ticks.parquet --output labelled.csv
```

---

## 10. Customisations

### Add an instrument to the catalogue

The shared signal-strategy instrument list lives at
`src/TradingTerminal.UI/TradeableInstrument.cs` (class `SignalInstrumentCatalog`). Add a
row to `All` following the existing pattern; instruments appear in every strategy's
dropdown on next launch.

The RSI strategy keeps its own wider catalogue at
`src/TradingTerminal.Strategies.Rsi/InstrumentCatalog.cs` — edit there for RSI only.

### Tune a strategy's parameters

Each per-strategy project's `<Name>StrategyViewModel.cs` declares parameters as
`[ObservableProperty]`s with defaults from the engine implementation's constructor. Edit
the defaults in the VM, OR — more commonly — change them at runtime in the strategy
window's Parameters panel before hitting Start.

### Add a new strategy

Follow the README's "Adding a new strategy" section. The fastest path: copy an existing
project, rename it, edit the VM to declare your parameter list and `BuildStrategy(contract)`
override, swap the engine class your VM constructs.

### Forward signals to a different sink (e.g. Slack, email)

Implement `INotificationTransport` in `src/TradingTerminal.Infrastructure/Notifications/<Channel>/`,
register it in `NotificationsServiceCollectionExtensions.AddNotifications`. The dispatcher
auto-discovers transports via `IEnumerable<INotificationTransport>`. Mirror the Telegram
or Discord transport for the shape.

---

## 11. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Login banner permanently red (IB) | TWS isn't running, or socket port doesn't match. Default TWS Paper is 7497. |
| `IB error 502: Couldn't connect to TWS` | API mode not enabled. **API → Settings → Enable ActiveX and Socket Clients**, add `127.0.0.1` to trusted IPs. |
| `IB error 326: client id is already in use` | Another client (Excel, another instance) has that ClientId. Pick a different integer. |
| `IB error 10089: requires additional subscription` | No real-time market-data sub on the contract. Switch `MarketDataType` to `3` (Delayed) on the login form. |
| NinjaTrader: `rc != 0` on connect | NT 8 isn't running, or **AT Interface** isn't enabled, or the `UseRealClient` flag is false. |
| NinjaTrader: `DllNotFoundException` | `NTDirect.dll` wasn't copied next to the assembly. Verify the build printed `NTDirect resolved from:`. |
| cTrader connect fails immediately | One of ClientId / ClientSecret / AccessToken / CtidTraderAccountId is missing or wrong. Logs pane has the exact `ProtoOAErrorRes`. |
| cTrader was working, now fails | Access token has expired (~30 days). Re-run the OAuth refresh, paste the new token. |
| Alpaca login fails immediately | API key id / secret wrong, or the key was minted for the other environment (paper key against live, or vice versa). Re-check the live toggle; regenerate the key from the dashboard if needed. |
| Alpaca: nothing happens on a non-stock / non-crypto symbol | The Alpaca client routes by `Contract.SecType` — only `STK` (stocks) and `CRYPTO` work today. Options aren't wired yet; route those through IB. |
| Alpaca: `sip` feed shows blank ticks | The consolidated SIP feed needs a paid Alpaca market-data subscription. Switch the feed dropdown to `iex` (free) on the login form. |
| Strategy window shows `AvalonDock.Layout.LayoutDocument` text | Stale build before the DockTab fix. `dotnet build` again. |
| Notifications not arriving | Open Logs pane — Telegram/Discord transports log failures there. Common: invalid bot token (Telegram), expired or malformed webhook URL (Discord). Hit **Send test** in the Settings tab to bypass strategy logic. |
| Ollama enricher silently doing nothing | The model isn't pulled, or Ollama isn't running. `ollama list` to confirm; `ollama serve` to start. Enricher always times out silently — it's deliberate so a slow LLM never backlogs the dispatcher. |
| Strategy doesn't fire signals on synth data | Many strategies are regime-specific (session-aware, gap-aware, sticky-touch, etc.). Synth random-walk doesn't reproduce those regimes. Use real recorded data via the Recorder tab. |
| Backtest CLI: `Unknown strategy 'foo'` | Run with `--strategy` set to one of the canonical ids; running the help (`daxalgo-backtest`) lists them all. |
| Build error after pulling: `_wpftmp.csproj` can't see a type | WPF MarkupCompilePass1 limitation. Clean `obj/` and rebuild, OR move the offending type to a referenced assembly. |
