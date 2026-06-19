# Machine Learning tools

> Last updated: 2026-06-18

Three time-series statistics windows under the top-level **Machine learning** menu. All of them are **offline analysis over historical bars** pulled from the canonical store via `IMarketDataRepository` — no live subscription, no broker round-trip beyond the history request, and fitting always runs off the UI thread. Each window follows the same conventions as the other tool windows: global instrument picker, timeframe dropdown, bar-count input, ScottPlot dark charts.

The windows are thin UI over reusable math in **`src/TradingTerminal.Core/Quant/TimeSeries/`** — every estimator is a plain testable class you can also call from a strategy or the backtester.

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
| Window projects | `src/TradingTerminal.Ml.Stationarity/`, `src/TradingTerminal.Ml.ArimaGarch/`, `src/TradingTerminal.Ml.KalmanFilter/` |
| Math | `src/TradingTerminal.Core/Quant/TimeSeries/` — `Ols`, `StationarityTests`, `SeriesTransforms`, `ArimaModel`, `GarchModel`, `NelderMead`, `KalmanFilters` |
| DI | each project ships its own `Add…Surface()` extension, called from `App.xaml.cs` |
| Tests | `tests/TradingTerminal.Tests/Quant/TimeSeriesMathTests.cs` (seeded synthetic series) |

## Limitations

- History-only: the windows fit whatever bars the connected broker (or the local store) can serve; they do not refresh on live ticks.
- ARIMA order search is bounded to a small grid; this is a diagnostic tool, not an auto-trader.
- GARCH is the plain (1,1) Gaussian flavour — no asymmetric/EGARCH variants.
