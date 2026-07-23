# Media checklist — screenshots & videos to capture

> Last updated: 2026-06-30 · **Status: capture pending**

This is the **master shot list** for the DaxAlgo Terminal documentation. Every screenshot and
video referenced anywhere in the docs has a reserved **slot** here, with an exact target
filename. Capture the images, name each file exactly as listed, drop them into the folders
below, and the matching `_coming soon_` placeholders in the docs get wired to real media.

Nothing in the docs currently links a missing image (so the public README never shows a broken
image icon) — each slot is a clean text placeholder until its file exists.

---

## How this works

1. **Capture** each shot listed in the tables below (the *What to capture* column says exactly
   what should be on screen).
2. **Name** the file exactly as in the *Filename* column — lower-case, hyphenated, no spaces.
3. **Drop** images into `images/` and videos into `images/video/` at the repo root.
4. Tell me it's done — I replace every `🖼️`/`🎬` placeholder with the real embed and commit.

### Folders

| Kind | Folder | Referenced from docs as | Referenced from root README as |
|---|---|---|---|
| Screenshots | `images/` | `../images/<name>.png` | `images/<name>.png` |
| Videos | `images/video/` | `../images/video/<name>.mp4` | `images/video/<name>.mp4` |

> **Video note (GitHub).** GitHub does **not** embed an `mp4` via `![](…)`. To show a video
> inline, either (a) drag the file into a GitHub issue/PR comment to get a
> `user-images.githubusercontent.com` URL and paste that, or (b) upload it to a Release and link
> it, or (c) keep a short animated **GIF** alongside the mp4 (`<name>.gif`) for inline preview and
> link the mp4 for full quality. The docs use a thumbnail-image-links-to-video pattern where a
> still is available.

### Placeholder convention (what you'll see in the docs)

A screenshot slot looks like this in the Markdown:

```markdown
> 🖼️ **Screenshot:** `images/shell-main.png` — main window with the strategy catalog tiled and
> the activity-log drawer closed.
```

A video slot looks like this:

```markdown
> 🎬 **Video:** `images/video/shell-tour.mp4` — 2–3 min tour: login → catalog → open a strategy →
> activity log.
```

Capture priority is flagged **P1** (front-page / most-viewed), **P2** (feature docs), **P3**
(nice-to-have, deep reference).

---

## 1. Shell & chrome  (doc: `user-guide.md`, root `README.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `shell-main.png` | Main window: strategy catalog tiled full-width, header strip + menu visible, activity-log drawer closed. The hero image. |
| P1 | `shell-strategy-card.png` | A single zoomed catalog card showing the data pills (L1/BAR/L2/TAPE) and classification pills (asset class · single/multi · broker chips) + a RESEARCH PAPER pill. |
| P2 | `shell-activity-log.png` | Activity-log drawer open at the bottom, a few coloured level pills (ENTRY/WARN/ERROR) and the filter box in use. |
| P2 | `shell-header-apimeter.png` | The API-meter dropdown open, showing the per-broker calls-vs-rate-limit breakdown. |
| P3 | `shell-header-strip.png` | Close-up of the header: wordmark, F1 HELP tile, API meter, CRYPTO/NYSE/LSE session badges, UTC + local clocks. |
| P3 | `shell-busy-overlay.png` | The loading curtain (BusyOverlay) shown while a window is opening. |

### Menus (each dropdown open)

| Pri | Filename | What to capture |
|---|---|---|
| P2 | `shell-menu-file.png` | **File** open: Reconnect to broker · Start QuestDB · Exit. |
| P2 | `shell-menu-view.png` | **View** open: Activity log · Theme ▶ (DaxAlgo Dark / DaxAlgo Light) · Customize theme… |
| P2 | `shell-menu-tools.png` | **Tools** open: Backtest Studio · Record live ticks · Advanced market regime · Correlation matrix · Live correlation matrix. |
| P2 | `shell-menu-plugins.png` | **Plugins** open: Manage strategy plugins… |
| P2 | `shell-menu-lse.png` | **LSE Tools** open: LSE backtester… |
| P2 | `shell-menu-charts.png` | **Charts** open: Charts · Order book · Volume footprint · Bookmap + VolBook. |
| P2 | `shell-menu-ml.png` | **Machine learning** open: Stationarity & differencing · ARIMA & GARCH · Kalman filter. |
| P2 | `shell-menu-quantconnect.png` | **QuantConnect / LEAN** open: Backtest runner · Projects · Data sync · Settings & status. |
| P2 | `shell-menu-ai.png` | **AI tools** open: Factor research · ML features · Backtest analysis · Market analyst · Paper Lab. |
| P2 | `shell-menu-data.png` | **Data** open: Market data archive · Archive history · Instant offload. |
| P2 | `shell-menu-settings.png` | **Settings** open: Notifications · Research (Paper Lab). |
| P3 | `shell-menu-help.png` | **Help** open: Support the developer · About DaxAlgo Terminal. |

---

## 2. Login  (doc: `getting-started.md`, `brokers.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `login-window.png` | ✅ **Captured.** The login window with the broker tiles and the Auto Connect option. |
| P2 | `login-services-panel.png` | ✅ **Captured.** The "Services & external dependencies" expander showing the probe rows (sidecar, Docker, IB TWS, NinjaTrader, Ollama) with re-check. |
| — | `video/login-window.mp4` | ✅ **Captured.** Walkthrough of all brokers, the services panel, and launching the terminal (wired into `getting-started.md` + `brokers.md`). |
| P3 | `login-broker-ib.png` | IB login form (host/port/clientId). |
| P3 | `login-broker-alpaca.png` | Alpaca login form (key/secret, paper/live toggle). |
| P3 | `login-broker-ctrader.png` | cTrader OAuth login form. |
| P3 | `login-broker-binance.png` | Binance tile (keyless — just Connect). |

---

## 3. Strategies  (doc: `strategies.md`)

For each strategy: a **window** shot (running, with its visualisation) and a **settings/params**
shot (the parameter panel before Start). 3D strategies should show the Helix view populated.

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `strategy-sigmaicflow-window.png` | Σ⁻¹·IC Order-Flow Optimizer running — signal table + weight bars + calibration/entry gate. |
| P2 | `strategy-sigmaicflow-settings.png` | Σ⁻¹·IC parameter panel. |
| P2 | `strategy-cumulativedelta-window.png` | Cumulative Delta Scalper — CVD line + footprint clusters. |
| P3 | `strategy-cumulativedelta-settings.png` | Cumulative Delta params. |
| P2 | `strategy-orderflowcube-window.png` | Order-Flow Cube — Helix 3D scatter + decay trail. |
| P3 | `strategy-orderflowcube-settings.png` | Cube params. |
| P2 | `strategy-orderflowsurfacespike-window.png` | Order-Flow Surface Spike — 3D z-score surface. |
| P3 | `strategy-orderflowsurfacespike-settings.png` | Surface Spike params. |
| P2 | `strategy-imbalanceheatfront-window.png` | Imbalance Heat Front — 3D L2 pressure surface + ridge. |
| P3 | `strategy-imbalanceheatfront-settings.png` | Heat Front params. |
| P2 | `strategy-indexkscoresurface-window.png` | Index K-Score Surface — per-constituent K surface. |
| P3 | `strategy-indexkscoresurface-settings.png` | K-Score params. |
| P2 | `strategy-indexregimegraph-window.png` | Index Regime Graph — pan/zoom node graph (Index ← stocks ← timeframes ← indicators). |
| P3 | `strategy-indexregimegraph-settings.png` | Regime Graph params/horizon. |
| P2 | `strategy-filteredorderflow-window.png` | Filtered Order-Flow Imbalance — filtered vs unfiltered OBI(T) + regime bins. |
| P3 | `strategy-filteredorderflow-settings.png` | Filtered OBI params. |
| P2 | `strategy-pressuremap-window.png` | 1-Minute Order-Flow Pressure Map — S&P 100/500 ticker × time heatmap with absorption/breakthrough flags. |
| P3 | `strategy-pressuremap-settings.png` | Pressure Map params. |

---

## 4. Charts & order-flow windows  (doc: `charts.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `chart-charts.png` | TradingView-style Charts window (WebView2) with SMA/EMA/RSI/MACD overlays. *(Windows only.)* |
| P2 | `chart-orderbook.png` | L2 Order Book ladder with per-level bars + cumulative depth. |
| P1 | `chart-orderbook-ml.png` | Order Book heatmap with the violet dotted ML forecast path in the right gutter, probability chips (P SPREAD↑/P DEPTH↓/P SWEEP↑) lit, and the ML-vs-OBI scoreboard visible in the strip. |
| P1 | `chart-footprint.png` | Volume Footprint with POC, stacked-imbalance highlights, fit curves + virtual predictor. |
| P1 | `chart-footprint-ml.png` | Volume Footprint forecast region with both predictors on: green/red dashed regression ghosts + violet dotted ML ghosts (width ∝ predicted volume, Δ̂ footer), ML vs Reg MAE/hit scoreboard visible in the stats panel. |
| P1 | `chart-bookmap.png` | Bookmap + VolBook: liquidity heatmap + trade dots + volume profile/VWAP/value area + CVD panel + DOM. |
| P1 | `chart-surfacelab.png` | 3D Surface Lab: a seasonality or cross-sectional surface in the Helix viewport with the peak pin, cutting planes and 2D slice charts visible. |
| P2 | `chart-surfacelab-robustness.png` | Same surface with the robustness heatmap on — green plateau vs red spike colouring. |

---

## 5. Tools  (docs: `backtesting.md`, `market-regime.md`, `user-guide.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `tool-backteststudio.png` | Backtest Studio: strategy + instrument + date range chosen, equity curve + stats panel populated. |
| P2 | `tool-backtest-quick.png` | The catalog right-click → Quick backtest (last 1 year) result. |
| P2 | `tool-advancedregime.png` | Advanced market regime board: 18 indicator rows × 8 timeframe columns, colour-coded + Trend needle. |
| P2 | `tool-correlation.png` | Correlation matrix (historical) with PCA panel. |
| P2 | `tool-correlation-live.png` | Live (EWMA) correlation matrix updating. |
| P2 | `tool-recording.png` | Record live ticks window. |
| P2 | `tool-lsebacktest.png` | LSE backtester window. |

---

## 6. Machine Learning menu  (doc: `machine-learning.md`)  *(Windows only)*

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `ml-stationarity.png` | Stationarity & differencing: price vs differenced series, ADF/KPSS verdicts, ACF/PACF bars. |
| P2 | `ml-arimagarch.png` | ARIMA & GARCH forecast with confidence bands + the volatility cone. |
| P2 | `ml-kalman.png` | Kalman filter: local-level/trend smoothing or the pairs hedge-β over time. |

---

## 7. AI tools  (docs: `ai-analyst.md`, `paper-lab.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `ai-marketanalyst.png` | AI Market Analyst: annotated candlestick + trend-channel chart + the agent verdict panel. |
| P2 | `ai-factorresearch.png` | Factor research window. |
| P2 | `ai-mlfeatures.png` | ML features window. |
| P2 | `ai-backtestanalysis.png` | Backtest analysis window. |
| P1 | `ai-paperlab.png` | Paper Lab: a paper URL resolved → repo candidates → job → save-as-strategy + confidence. |

---

## 8. QuantConnect / LEAN  (doc: `quantconnect.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P2 | `qc-backtest.png` | QuantConnect backtest runner with a result. |
| P3 | `qc-projects.png` | Projects window. |
| P3 | `qc-datasync.png` | Data sync window. |
| P3 | `qc-settings.png` | Settings & status (LEAN CLI path, cloud token state). |

---

## 9. Data, archive, plugins, theme, settings  (docs: `storage.md`, `plugins.md`, `theme-studio.md`, `notifications.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P2 | `data-archive-settings.png` | Market-data archive settings (Telegram offloader config). |
| P3 | `data-archive-history.png` | Archive history window. |
| P1 | `plugins-manager.png` | Plugin Manager: installed plugins list + install-from-file + signature/trust status. |
| P2 | `theme-studio.png` | Theme Studio: live palette editor with a token being edited and the preview updating. |
| P2 | `settings-notifications.png` | Notifications settings: Telegram + Discord + Ollama enricher toggles. |
| P3 | `settings-research.png` | Research (Paper Lab) settings: enable sidecar + loopback URL. |

---

## 10. Videos  (docs: root `README.md`, `user-guide.md`)

| Pri | Filename | What to capture |
|---|---|---|
| P1 | `video/shell-tour.mp4` | 2–3 min: launch → login (or offline Simulated) → catalog tour → open a strategy → activity log → close. The README hero video. |
| P2 | `video/backtest-studio.mp4` | Run a backtest end-to-end in Backtest Studio and read the stats. |
| P2 | `video/order-flow-3d.mp4` | An Order-Flow Cube / Surface Spike session rotating the 3D view. |
| P3 | `video/paper-lab.mp4` | Paper Lab: paper → repro job → save as strategy → backtest. |
| P3 | `video/ai-analyst.mp4` | Ask the AI Market Analyst for a read on an instrument. |

> You already have a draft `test/homescreenvideo.mp4` and `test/homescreen.png` — those can seed
> `video/shell-tour.mp4` and `shell-main.png` once finalised.
