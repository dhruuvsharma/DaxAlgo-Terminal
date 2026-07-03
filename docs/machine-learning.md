# Machine Learning tools

> Last updated: 2026-06-30 · **Windows build only**

> **Windows only.** The Machine-learning menu and its three windows ship in the Windows/WPF build.
> The Linux/Avalonia tree doesn't include them yet (the underlying maths library is shared, but the
> windows aren't ported).

### In plain terms

These three tools answer three classic questions about a price series using only **historical bars**
— they *analyse*, they don't trade:

- **Stationarity & differencing** — *"Is this series steady enough to model, and if not, how do I make
  it so?"* Raw prices wander off; most statistics need a series that hovers around a stable average.
  This tool checks, and recommends the gentlest fix.
- **ARIMA & GARCH** — *"What's a sensible forecast, and how jumpy is it likely to be?"* ARIMA projects
  the **price**; GARCH projects the **volatility** (is a calm or stormy stretch ahead?).
- **Kalman filter** — *"What's the real signal hiding under the noise?"* — smoothing a wiggly series,
  or tracking how the relationship between **two** instruments drifts over time (the heart of pairs
  trading).

You don't need to know the maths to use them — each window shows a plain verdict and a chart. The
exact formulas, derived step by step, are in the [math reference](math-reference.md#15-time-series--corequanttimeseries-machine-learning-menu).

Technically, all three are **offline analysis over historical bars** pulled from the canonical store via `IMarketDataRepository` — no live subscription, no broker round-trip beyond the history request, and fitting always runs off the UI thread. Each window follows the same conventions as the other tool windows: global instrument picker, timeframe dropdown, bar-count input, ScottPlot dark charts.

> **Looking for *live* machine learning?** Two chart windows embed **online learners** (recursive least squares, trained continuously, warm-started from the local store, scored live against a classical baseline):
> - the [Volume footprint chart](charts.md#ml-predictor) forecasts the next bars' POC/volume/delta vs the chart's regression predictor (`Core/Ml/FootprintNextBarPredictor`, [math §4.1](math-reference.md#41-volume-footprint--tradingterminalvolumefootprint));
> - the [Order book window](charts.md#ml-micro-forecast) forecasts the microprice path 250 ms–5 s out plus spread-widening / depth-drain / sweep-jump probabilities vs the queue-imbalance rule (`Core/Ml/OrderBookMicroPredictor`, [math §4.3](math-reference.md#43-order-book--tradingterminalorderbook)).

The windows are thin UI over reusable math in **`src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/`** — every estimator is a plain testable class you can also call from a strategy or the backtester.

## Stationarity & differencing

**Machine learning → Stationarity & differencing…**

Answers the first question of any time-series workflow: *is this series stationary, and which transform makes it so?*

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

1. Pick an instrument, timeframe, and bar count (default 500).
2. Pick a **transform**: none, log, first difference, log returns, or **fractional differencing** (fixed-window, with a `d` input, default 0.4 — the "keep memory, kill the trend" option).
3. **Run.** The window shows:
   - **ADF and KPSS verdict cards** — test statistic, critical values, and a colored stationary / non-stationary verdict. The two tests have opposite null hypotheses, so the **agreement line** tells you when they actually concur and when the answer is "inconclusive".
   - **Rolling mean / std bands** over the transformed series — visual stationarity check.
   - **ACF chart** with the white-noise confidence band.
   - **Recommendation sweep** — every transform is tested behind the scenes and the window recommends the mildest one that passes.

Implementation: `StationarityTests` (ADF with AIC lag selection and MacKinnon critical values; KPSS with Bartlett long-run variance; ACF + white-noise band) and `SeriesTransforms` (transforms + O(n) rolling moments).

## ARIMA & GARCH

**Machine learning → ARIMA & GARCH…**

Classical forecast + volatility modelling on one instrument.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

1. Pick instrument / timeframe / bar count, and either set **p, d, q** by hand or leave **Auto order** ticked to AIC-search the order grid.
2. Set the forecast **horizon** (default 20 bars).
3. **Run.** The window shows:
   - The fitted **ARIMA(p,d,q)** equation with coefficients, t-stats, and AIC/BIC.
   - A **price forecast chart** with the 95% confidence band (psi-weight bands built in log space and integrated back to level, so the band is log-normal and never goes negative).
   - A **GARCH(1,1) conditional-volatility chart** against the long-run variance level, plus the fitted parameters and a colored **persistence** read-out (α + β close to 1 = shocks decay slowly).

Implementation: `ArimaModel` (Hannan–Rissanen two-stage OLS), `GarchModel` + `NelderMead` (Gaussian MLE), `Ols` (multi-regressor normal equations with t-stats).

## Kalman filter

**Machine learning → Kalman filter…**

State-space filtering in three modes (the **Mode** dropdown):

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Mode | State | Use it for |
|---|---|---|
| Local level | smoothed price level | denoising a single series |
| Local linear trend | level + slope | trend extraction with a velocity read-out |
| Dynamic regression | time-varying hedge ratio β between **two** instruments | pairs trading — the second instrument picker appears in this mode |

The **Q/R ratio** knob trades responsiveness against smoothness (process noise vs observation noise). Dynamic-regression mode charts the evolving **β** and the **spread z-score** — the classic pairs entry/exit signal. Every mode reports **innovation diagnostics** (whiteness check on the one-step-ahead errors): if the innovations aren't white, the model is mis-specified, and the window says so.

Implementation: `KalmanFilters` in `Core/Quant/TimeSeries/`.

## Code reference

| What | Where |
|---|---|
| Window projects | `src/windows/MachineLearning/TradingTerminal.Ml.Stationarity/`, `…Ml.ArimaGarch/`, `…Ml.KalmanFilter/` |
| Math | `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/` — `Ols`, `StationarityTests`, `SeriesTransforms`, `ArimaModel`, `GarchModel`, `NelderMead`, `KalmanFilters` |
| DI | each project ships its own `Add…Surface()` extension, called from the shell's `App` startup |
| Tests | `tests/TradingTerminal.Tests/Quant/TimeSeriesMathTests.cs` (seeded synthetic series) |

## Limitations

- History-only: the windows fit whatever bars the connected broker (or the local store) can serve; they do not refresh on live ticks.
- ARIMA order search is bounded to a small grid; this is a diagnostic tool, not an auto-trader.
- GARCH is the plain (1,1) Gaussian flavour — no asymmetric/EGARCH variants.
