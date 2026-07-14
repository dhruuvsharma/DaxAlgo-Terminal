---
id: quant-math
name: Quant math (numerically stable forms)
triggers: ema, sma, moving average, variance, stdev, standard deviation, z-score, zscore, mean reversion, ornstein, uhlenbeck, half-life, cointegration, correlation, regression, kalman, garch, arima, volatility, vol, sharpe, kurtosis, skew, percentile, quantile, vpin, kyle, lambda, hurst, entropy, statistic, distribution, estimator, smoothing, filter
---

# Quant math — the stable forms

Use these. The textbook forms overflow, drift, or re-scan windows they don't need to.

## Online estimators (O(1) per tick, no re-summing)

```csharp
// EMA — seed on the first sample, never with 0 (that biases the whole series toward zero).
if (!_seeded) { _ema = x; _seeded = true; }
else          { _ema += 2.0 / (period + 1) * (x - _ema); }

// Welford variance — numerically stable; the naive sum-of-squares loses precision fast.
_count++;
var d  = x - _mean;
_mean += d / _count;
_m2   += d * (x - _mean);
var variance = _count > 1 ? _m2 / (_count - 1) : 0.0;
var stdev    = Math.Sqrt(variance);

// EWMA variance — the rolling equivalent, for a regime that changes.
_ewmaVar = lambda * _ewmaVar + (1 - lambda) * (x - _ewmaMean) * (x - _ewmaMean);
```

For a *windowed* mean/variance, keep a fixed-size ring buffer and add/subtract at the edges — never
re-sum the window on every tick.

## Normalisation

```csharp
var z = (x - mean) / Math.Max(1e-9, stdev);   // ALWAYS floor the denominator
var r = Math.Log(p / prevP);                  // log returns: additive, scale-free
```

A z-score on fewer than ~30 samples is noise. Gate on a warm-up count before you trade it.

## Mean reversion (Ornstein-Uhlenbeck)

`dX = theta (mu - X) dt + sigma dW`. Fit by OLS of `X_{t+1}` on `X_t`:

```
X_{t+1} = a + b X_t + eps
theta   = -ln(b) / dt            // speed of reversion  (b in (0,1) or there is no reversion)
mu      = a / (1 - b)            // long-run mean
halfLife = ln(2) / theta         // the number that actually matters
```

If `b >= 1` the series is not mean-reverting — do not trade the spread. Report the half-life; a strategy
whose half-life is longer than its holding period is not a mean-reversion strategy.

## Microstructure statistics

- **VPIN / toxicity**: bucket by *volume*, not time; VPIN = mean over buckets of
  `|buyVol - sellVol| / bucketVolume`, in [0, 1].
- **Kyle's lambda** (price impact): OLS slope of price change on signed volume over a window. Rising
  lambda = a thinning book.
- **Realised volatility**: `sqrt(sum of squared log returns)` over the window, annualised only if you
  actually mean to annualise it.
- **Spread stats**: keep mean and stdev of `(ask - bid)` with Welford; "the spread blew out" means
  `spread > mean + k*stdev`, not a hard tick count that is wrong on the next instrument.

## Rules of thumb

- Every threshold that is an absolute number is a bug waiting for a different instrument. Normalise by
  volatility, spread, or tick size.
- Warm up before you trade: no estimator is meaningful on its first few samples.
- Guard every division. `Math.Max(1e-9, denominator)` costs nothing.
- Prefer one pass and O(1) state. `OnTickAsync` runs per tick, and a backtest replays millions of them.
