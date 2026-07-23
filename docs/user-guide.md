# User guide

> Last updated: 2026-06-30

A daily-use walkthrough for **using** the terminal, written so you can follow it with no trading,
coding, or maths background. For installation and the first launch, see
[getting-started.md](getting-started.md). For per-broker setup, see [brokers.md](brokers.md). Each
feature has a deep-dive doc — followed by a cross-link.

> 🖼️ **Screenshot:** `images/shell-main.png` — the main window.
> 🎬 **Video:** `images/video/shell-tour.mp4` — a 2–3 minute walkthrough of everything below.

---

## 1. Launching and logging in

Run the app (see [getting-started.md](getting-started.md)):

```powershell
dotnet run --project src/windows/Shell/TradingTerminal.App
```

The **login window** opens with a tile per data source: **Interactive Brokers, NinjaTrader, cTrader,
Alpaca, Ironbeam, London Strategic Edge, Upstox, Binance, Coinbase, Bybit, Kraken, OKX**, plus the
always-available offline **Simulated** feed. You can connect **several at once** — sessions are
concurrent, and each instrument's data (history, live ticks, depth, trade tape, connection state)
routes through the broker it belongs to.

- The **Binance** tile needs **no account** — click Connect and live crypto data flows.
- Tick **Auto Connect** to have every broker with saved credentials connect automatically on future
  launches. One dead broker never blocks the others.
- A **Services & external dependencies** expander probes the optional helpers (Python sidecar,
  Docker, IB TWS, NinjaTrader, Ollama) and tells you if any aren't running.

After **Sign in**, the main shell opens. If a login fails, open the **Activity log** drawer (bottom)
— every broker error is logged there with enough detail to act on.

---

## 2. The main shell

Every strategy, tool and chart opens as **its own window**. The main window itself is a full-width
**strategy catalog** with a collapsible log drawer.

```
+--------------------------------------------------------------------+
| DAXALGO TERMINAL · F1 HELP · API meter · CRYPTO/NYSE/LSE · UTC clock|
| File View Tools Plugins LSE-Tools Charts Machine-learning           |
|        QuantConnect/LEAN AI-tools Data Settings Help                |
| [Disconnect banner — only when not Connected]                      |
+--------------------------------------------------------------------+
|  STRATEGY CATALOG            (double-click to open)        N=12    |
|  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐               |
|  │ Sigma-IC │ │ CumDelta │ │ Toxicity │ │ OU       │   …  (tiled)  |
|  │ pills    │ │ pills    │ │ pills    │ │ pills    │               |
|  └──────────┘ └──────────┘ └──────────┘ └──────────┘               |
+--------------------------------------------------------------------+
| ▴ ACTIVITY LOG   (collapsible drawer — closed by default)          |
+--------------------------------------------------------------------+
| ●Connected            LIVE 2 brokers                    12:34:56   |
+--------------------------------------------------------------------+
```

- **Header strip** — the wordmark, an **F1 HELP** tile, the live **API-call meter** (click it for a
  per-broker breakdown of calls vs each broker's rate limit — green/amber/red on the hottest one),
  approximate market-session badges (**CRYPTO** 24/7, **NYSE**, **LSE** — open/closed), and **UTC +
  local** clocks.
- **Strategy catalog** — a tiled grid of strategy cards. Each shows the name, id, description, and the
  data/classification **pills** (explained in [strategies.md](strategies.md#reading-the-catalog-cards)).
  **Double-click** a card (or right-click → *Open*) to launch that strategy in its own window.
- **Activity-log drawer** — the one universal log (system messages from Serilog + per-strategy/window
  entries), collapsed by default. Click the **▴ ACTIVITY LOG** strip (or *View → Activity log*) to
  slide it open. Filter by typing in the box; copy rows with Ctrl+C. **There are no per-window log
  panels — everything routes here.**
- **Status bar** — connection-state dot (green = connected), the count of live brokers, and the clock.

> 🖼️ **Screenshot:** `images/shell-activity-log.png` — the activity-log drawer open with coloured
> level pills.

---

## 3. The menus — a complete tour

Every window in the app lives under one of these menus. Re-selecting an already-open window just
brings it to the front (windows are single-instance).

### File
| Item | What it does |
|---|---|
| **Reconnect to broker** | Force a reconnect of the active broker session. |
| **Start QuestDB** | Launch Docker (if needed) and the QuestDB container, then switch on high-rate tick persistence — without restarting. |
| **Exit** | Close the app. |

### View
| Item | What it does |
|---|---|
| **Activity log** | Show/hide the bottom log drawer. |
| **Theme** | Switch between **DaxAlgo Dark** and **DaxAlgo Light**. |
| **Customize theme… (Theme Studio)** | Open the live colour editor — recolour any part of the app and save your own theme. See [theme-studio.md](theme-studio.md). |

### Tools
| Item | What it does |
|---|---|
| **Backtest Studio** | The full backtest workbench — run any strategy on historical data and read the stats. See [backtesting.md](backtesting.md). |
| **Record live ticks** | Capture the live feed to a file you can replay/backtest later (see §6 below). |
| **Advanced market regime** | An 18-indicator × 8-timeframe board telling you whether an instrument is trending/ranging/volatile across timeframes. See [market-regime.md](market-regime.md#advanced-market-regime-board). |
| **Correlation matrix** | How a basket of instruments move together (historical). See [math-reference.md](math-reference.md#31-correlation-matrix--tradingterminalcorrelation). |
| **Live correlation matrix** | The same, updating live (EWMA). |

### Plugins
| Item | What it does |
|---|---|
| **Manage strategy plugins…** | Install / view third-party strategy plugins. See [plugins.md](plugins.md). *(Windows.)* |

### LSE Tools
| Item | What it does |
|---|---|
| **LSE backtester** | A backtester that pulls historical bars straight from the free London Strategic Edge feed. |

### Charts
| Item | What it does |
|---|---|
| **Charts** | TradingView-style candlestick charting with indicator overlays. *(Windows.)* |
| **Order book** | The live L2 depth ladder. |
| **Volume footprint** | A bid/ask cluster chart with curve-fit POC predictors. |
| **Bookmap + VolBook** | The combined liquidity-heatmap + volume-profile + DOM window. |

Full per-window reference: [charts.md](charts.md).

### Machine learning *(Windows)*
| Item | What it does |
|---|---|
| **Stationarity & differencing** | Is this series "tradeable-stable", and how to transform it (ADF/KPSS/ACF). |
| **ARIMA & GARCH** | Forecast the price with a confidence band, and model its volatility. |
| **Kalman filter** | Smooth a series or track a time-varying pairs hedge ratio. |

Deep dive (with the maths from scratch): [machine-learning.md](machine-learning.md).

### QuantConnect / LEAN
| Item | What it does |
|---|---|
| **Backtest runner / Projects / Data sync / Settings & status** | An optional bridge to the open-source LEAN backtester (runs as a separate process). Experimental — see [quantconnect.md](quantconnect.md). |

### AI tools
| Item | What it does |
|---|---|
| **Factor research** | Inspect microstructure features and whether they predict the next move (§7). |
| **ML features** | Feature-engineering workbench over recorded ticks. |
| **Backtest analysis** | AI-assisted read of a backtest's results. |
| **Market analyst** | A four-agent AI that gives a plain-language Long/Short/NoCall read on an instrument. See [ai-analyst.md](ai-analyst.md). |
| **Paper Lab** | Turn a research paper into a backtestable strategy, safely. See [paper-lab.md](paper-lab.md). |

### Data
| Item | What it does |
|---|---|
| **Market data archive** | Configure the optional Telegram offloader that backs up + prunes your local store. |
| **Archive history** | Browse what's been archived. |
| **Instant offload (all pending)** | Push everything pending to the archive now. See [storage.md](storage.md). |

### Settings
| Item | What it does |
|---|---|
| **Notifications** | Telegram, Discord, and the Ollama commentary enricher. See [notifications.md](notifications.md). |
| **Research (Paper Lab)** | Enable the Paper Lab reproduction sidecar + loopback URL. |

### Help
| Item | What it does |
|---|---|
| **Support the developer** | How to send feedback and support the project. |
| **About** | Version and credits. |

---

## 4. Running a strategy live (signal mode)

Every strategy opens as a separate window; the pattern is the same for all:

1. **Double-click** the strategy card in the catalog. Its window opens.
2. **Pick an instrument** from the dropdown (the shared catalog covers common ETFs, big US stocks,
   continuous futures, and spot FX/crypto). To add your own, see §8.
3. **Edit the parameters** in the Parameters panel. Each knob (period, threshold, lookback…) comes
   from the underlying logic; defaults are sensible but not optimised for any instrument.
4. **Press Start.** The window subscribes to the live feed for that instrument. Whenever the strategy
   *would* act, a row appears in its **signal log** **and** a notification is published.
5. **Press Stop** to flatten the strategy's internal state. **Clear log** clears the in-window grid
   (already-sent notifications stay sent).

**No order ever leaves the terminal.** Every strategy runs in "signal mode" — it tells you what it
*would* do; you act (or not) in whatever platform you actually trade from. See
[strategies.md](strategies.md) for the catalog and each strategy explained.

---

## 5. Quick backtest from the catalog

Want to see how a strategy would have done recently, without opening Backtest Studio? **Right-click a
catalog card → Quick backtest (last 1 year).** The app pulls a year of history for a default
instrument and runs it, showing the equity curve and stats. For full control (instrument, dates,
fees, risk caps), use **Tools → Backtest Studio** — see [backtesting.md](backtesting.md).

---

## 6. Recording live ticks

Build your own tick archive to replay/backtest later:

1. **Tools → Record live ticks.**
2. Pick an instrument and an output `.parquet` path (default under
   `%LOCALAPPDATA%\DaxAlgo Terminal\recordings\`).
3. **Start** — the live feed streams into the file; the grid shows recent ticks and a running count.
4. **Stop** flushes and closes the file. The result is directly usable as `--data` for the
   `daxalgo-backtest` CLI or in Backtest Studio.

L1 today; depth recording isn't wired yet.

---

## 7. Factor research (AI tools)

The day-to-day "does this feature predict anything?" loop:

1. **AI tools → Factor research.**
2. **Browse** for a parquet tick file (synthetic or recorded).
3. Set **BarTicks** (ticks per bar), **VolWindow**, **ForwardBars** (the prediction horizon).
4. **Compute.** You get a **correlation matrix** of standard features (spot redundant ones with
   |ρ| > 0.7) and a **decile sort** of a chosen feature against forward returns — a monotone staircase
   means predictive, flat means no edge at that horizon.

---

## 8. Customisations

### Add an instrument to the catalog
The shared instrument list lives at `src/windows/Shell/TradingTerminal.UI/…SignalInstrumentCatalog…`.
Add a row following the existing pattern; it then appears in every strategy and tool picker on next
launch.

### Tune a strategy's parameters
Change them at runtime in the strategy window's Parameters panel, or edit the defaults in that
strategy's view-model. For systematic searches, use the CLI's `sweep` / `walkforward` — see
[backtesting.md](backtesting.md).

### Add a new strategy, broker, or notifier
See [contributing.md](contributing.md) for the recipes and the layering rules.

---

## 9. CLI cheat sheet

The backtest engine is also a headless CLI for scripting/CI. Build it once:

```powershell
dotnet build src/windows/Backtest/TradingTerminal.Backtest.Cli
```

Then call `daxalgo-backtest <command>` (`run` / `synth` / `sweep` / `walkforward` / `mc` / `tca` /
`features`). Full reference and a worked pipeline: [backtesting.md](backtesting.md#cli-subcommand-reference).

---

## 10. Troubleshooting

For symptom → fix tables across every subsystem, see [troubleshooting.md](troubleshooting.md). The
most common pitfalls:

- TWS isn't running, or its socket port doesn't match `appsettings.json` (default TWS Paper is 7497).
- NinjaTrader 8 isn't running, or **Tools → Options → AT Interface → AT Interface enabled** isn't
  ticked.
- A cTrader access token has expired (~30 days) — re-run the OAuth refresh.
- An Alpaca key was minted for the wrong environment (paper vs live) — re-check the live toggle.
- Postgres set as the store provider but Docker isn't running — the app falls back to SQLite; check
  the Activity-log drawer.
