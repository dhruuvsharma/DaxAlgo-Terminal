---
id: quant-math
name: Quant math (the library, and the judgement it cannot make for you)
triggers: ema, sma, moving average, wilder, rsi, atr, macd, bollinger, vwap, variance, stdev, standard deviation, z-score, zscore, mean reversion, ornstein, uhlenbeck, half-life, cointegration, pairs, hedge ratio, correlation, beta, regression, kalman, garch, arima, volatility, vol, sharpe, sortino, drawdown, calmar, profit factor, expectancy, kurtosis, skew, percentile, quantile, vpin, kyle, lambda, microprice, imbalance, toxicity, hurst, entropy, statistic, distribution, estimator, smoothing, filter, indicator
---

# Quant math

**Do not hand-roll these.** `DaxAlgo.Sdk.Quant` is ambient — no `using` needed — and every estimator
in it is streaming, warm-up gated, guarded against non-finite input, and pinned by tests. Writing the
loop yourself costs output tokens, and a wrong one is invisible: an RSI smoothed with an EMA compiles,
runs, draws and trades. It is simply not an RSI, and nothing downstream will ever tell you.

## The shape they all share

Construct in a field, `Update` in a callback, read `Value`, and **gate on `IsReady`**.

```csharp
private readonly Ema _fast = new(12);
private readonly Atr _atr = new(14);
private readonly ZScore _z = new(200);

public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
{
    _fast.Update(bar.Close);
    _atr.Update(bar);
    _z.Update(bar.Close - _fast.Value);

    if (!_z.IsReady || !_atr.IsReady) return Task.CompletedTask;   // warm-up, every time
    ...
}
```

`IsReady` is not politeness. An estimator's first samples are not a small version of its converged
value, they are noise with the same type — a 200-period z-score on its third bar reads near zero
exactly when the series is most unusual, because with three points the extremes *are* the sample.

`Reset()` on a session boundary or an instrument change. `Vwap` in particular: carried across
sessions it anchors to yesterday's volume and drifts further from anything tradeable every bar.

## Picking the right one

| You want | Use | Not |
|---|---|---|
| A smoothed level | `Ema`, `Sma`, `Dema`; `KalmanLevel` adapts | a `List` and `.Average()` |
| A classic oscillator | `Rsi`, `Macd`, `BollingerBands` | your own loop |
| RSI/ATR smoothing | `Wilder` | `Ema` — different α, different crossings |
| Dispersion over a window | `RollingWindow.StandardDeviation` | a running sum of squares |
| Dispersion over the session | `Welford` | the textbook variance formula |
| Dispersion in a moving regime | `EwmaVariance` | a long window |
| "Is this move big" | `ZScore`, or `RollingWindow.ZScoreOf` | an absolute threshold |
| Position in a range | `RollingWindow.PositionOf` | hand-rolled stochastic |
| A robust threshold | `RollingWindow.Quantile` | a tick count |
| Volatility for sizing | `Atr` (bars) or `RealizedVolatility` (returns) | high − low |
| A relationship | `OnlineRegression` (read `RSquared`), `RollingCorrelation` | correlation alone |
| A drifting relationship | `KalmanHedgeRatio` | a rolling regression |
| "Does this spread revert" | `OrnsteinUhlenbeck.IsMeanReverting` and `HalfLife` | eyeballing the chart |
| Who traded | `TradeClassifier` | assuming the print side |
| Flow pressure | `OrderFlowImbalance` | raw contract counts |
| Toxic flow | `Vpin` | a time-bucketed ratio |
| Cost of size | `KyleLambda`, `Book.SweepPrice` | the visible depth sum |
| Fair value in a book | `Book.Microprice` | the mid |
| "Is the spread wide" | `SpreadStats.IsWide()` | a tick count |
| A tear sheet | `EquityStats`, `TradeStats` | computing Sharpe inline |

## The judgement a library cannot make

**Normalise everything.** A threshold that is an absolute number is a bug waiting for a different
instrument. Divide by ATR, by the spread's own distribution, by a quantile — never by a constant you
chose while thinking about one symbol. This is the single most common reason a strategy that worked
in a backtest stops working on the next ticker.

**Read the normalised member.** `BollingerBands.PercentB` and `.Width` transfer between instruments; the raw band prices do not.

**A slope is not evidence.** `OnlineRegression` fits a line through anything. `RSquared` and
`SlopeTStatistic` are what separate a hedge ratio from a random number with two decimal places.

**Mean reversion needs a test, not a coefficient.** `OrnsteinUhlenbeck` gates on the Dickey-Fuller
statistic rather than on `Phi < 1`, because least squares is biased downward under a unit root — so
`Phi < 1` is the *expected* reading on a random walk, and R² is *near one* on a walk (the best
predictor of the next level is the current one) and only ~0.25 on a fast-reverting series. Ranking by
fit quality prefers exactly the series you must reject. Use `IsMeanReverting`, and check that
`HalfLife` is shorter than your intended holding period — a reversion that arrives after you have
closed is not a strategy.

**Compute in the callbacks, not in `Draw`.** `Draw` may run more than once per frame and blocks the
UI. Keep what the picture needs in a bounded field.

**Say what the numbers mean.** A strip of `Tiles.Draw` fed from `EquityStats` and `TradeStats` — P&L,
Sharpe, max drawdown, hit rate — turns a chart into something a trader keeps open. Both exemplars show
the shape.

## What is deliberately absent

No GARCH, no ARIMA, no Hurst exponent, no cointegration test beyond the univariate one above. They
need more history than a live window holds and more fitting than a per-tick callback can afford. If a
brief truly needs one, say so and build the simplest thing that answers the question — usually
`EwmaVariance` in place of GARCH, and `OrnsteinUhlenbeck` on the spread in place of Engle-Granger.
