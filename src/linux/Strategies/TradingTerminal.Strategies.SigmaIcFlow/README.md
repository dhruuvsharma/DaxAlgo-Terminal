# Σ⁻¹·IC Order-Flow Optimizer

`TradingTerminal.Strategies.SigmaIcFlow` — live signal window for strategy id **`sigma.ic.flow`**.

> Formerly "APEX Microstructure Scalper v2" (renamed 2026-06-19). The engine-side class is still
> `ApexScalperStrategy` in `Infrastructure/Backtest/Strategies/` — internal name retained.

> A **trade-tape-primary** order-flow scalper. Twelve microstructure signals are fused with
> mean-variance optimal weights ($w \propto \Sigma^{-1}\cdot IC$), the composite is calibrated onto
> realized forward return by isotonic regression, and a signal fires only when the calibrated edge
> clears the full round trip (spread + fees + conditional slippage) **and** a first-passage
> expected-value check. **Data/signals only — does not place orders.**

## v2.1 upgrades (2026-06-19)

A quant pass tightened the math and added a forward-looking signal:

- **Kyle-λ is now 2SLS instrumental-variable**, not OLS. Current flow Δ_t is instrumented by its lag
  Δ_{t−1} (Stage 1: Δ_t ~ Δ_{t−1}; Stage 2: r_t ~ Δ̂_t) to purge the contemporaneous
  endogeneity/simultaneity bias that inflates a naive OLS λ̂. The first-stage R² is surfaced as an
  instrument-strength diagnostic and folds into the signal's confidence.
- **TAPE_SPEED is now a Hawkes self-exciting intensity** (λ(t) = μ + Σ α·e^{−β(t−tᵢ)}), z-scored —
  a sharper, less laggy "tape speed" than a flat rolling arrival count.
- **12th signal — PRED_NODE (predicted node migration).** A constant-velocity **Kalman filter**
  tracks each of the buy / sell / total POC (price + velocity) and forecasts them `n` bars ahead.
  Expanding predicted wedge ⇒ trend (follow the predicted total-POC drift); converging ⇒ fade the
  stretch. Scaled by prediction confidence 1 − σ²_pred/σ²_bar.
- **Dynamic exits.** When PRED_NODE is confident, the bracket targets the predicted buy POC (long) /
  sell POC (short) and stops at the opposite predicted node, falling back to the structure/ATR logic
  when the forecast variance is too high.
- **First-passage gap penalty.** The continuous-path win probability is deducted by the empirical
  frequency (last 100 bars) of bar ranges large enough to gap through the nearer barrier — jump risk
  the Brownian model can't see.
- **Adaptive slippage.** The cost-gate slippage is surcharged by immediate book pressure on the side
  being traded into: `slip = condSlip·(1 + oppositeOBI)`.
- **Constant-volume bars** are now selectable (default still 1-minute time bars); the composite is
  hard-clipped to [−3, 3] before calibration; weights recompute every 50 bars over a 1500-bar
  window; and the Kelly cap is session-aware (0.25 overlap, 0.10 Asian).

## Data requirements & feed quality

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | optional | primary |

Every flow signal is computed from true aggressor-side volume off the **trade tape**
(`OnTradeAsync`). Each print's aggressor is taken from the feed, or classified by quote/tick rule
against the prevailing bid/ask. The feed quality multiplies every signal's confidence:

| Feed | Quality $q$ | Meaning |
|---|:--:|---|
| `RealTape` | $1.0$ | genuine prints; full confidence |
| `SyntheticL1` | $\approx 0.4$ | pseudo-prints manufactured from L1 quotes — heavily discounted; the first real print switches the engine off this path for good |
| `None` | — | warming; all signals invalid |

L2 depth is **optional**: it powers only the OBI signal, which goes invalid (rather than degrading
the composite) whenever no fresh depth snapshot exists.

Windowed flow statistics run in **volume time** — constant-volume buckets whose size adapts to the
median bar volume — so the estimators don't distort between quiet and busy regimes. Wall-clock time
survives only in signal TTLs and session gates.

## Pipeline

```
trade prints ──► footprint bars (volume-at-price, shared with the chart)
              ├► volume-time buckets ──► VPIN
              ├► per-bar features (Δ, CVD, centroids, POC) ──► line fits, Kyle-λ
              └► tape arrival window ──► tape speed
depth stream ──► OBI (only when fresh)

12 signal scores Sᵢ ∈ [−3, 3] ──► w = Σ⁻¹·IC blend ──► composite C
C ──► isotonic g(C) = E[r | C] ──► cost gate ──► first-passage EV ──► ¼-Kelly size
```

Signals recompute on each completed candle (default 15 s–1 m); between closes the dashboard
refreshes off cached signal state with live TTL ages.

## The 12 signals

Every signal returns a tuple $(S, \text{conf}, \text{dir}, \text{valid})$ with score
$S \in [-3, 3]$ and confidence pre-multiplied by feed quality $q$.

### Flow signals

**DELTA** — z-score of the bar's signed volume plus a z-score of its acceleration:

$$S = 0.6\,\hat S(z_\Delta) + 0.4\,\hat S(z_{\dot\Delta}), \qquad
z_\Delta = \frac{\Delta_t - \bar\Delta}{\sigma_\Delta}, \quad
\dot\Delta_t = \Delta_t - \Delta_{t-p}$$

where $\hat S(z) = \mathrm{clamp}(3z/\theta, -3, 3)$ with threshold $\theta = 2$.

**VPIN** — toxicity over constant-volume buckets. For each bucket, with buy fraction $f$:

$$\tau = 2\left| f - \tfrac12 \right|, \qquad
\mathrm{VPIN} = \frac{1}{L}\sum_{j=1}^{L} \tau_j$$

Magnitude scales with VPIN; the *sign* follows the most recent bucket's net flow direction
($f > 0.5$ ⇒ buy-toxic ⇒ positive).

**FOOTPRINT** — stacked-imbalance contrast from the Core footprint extractor (3:1 diagonal rule,
the same `FootprintFeatures.BuildBar` the cluster chart renders). Average of
$\mathrm{clamp}(\text{stackedBuy},0,3) - \mathrm{clamp}(\text{stackedSell},0,3)$ over the last 3
completed bars, nudged ±0.5 by the forming bar's leading stacks.

**TAPE_SPEED** — arrival-rate z-score with a directional gate. With rate $r$ over the window and
up-tick fraction $u$: fires only when $z_r > 2$; long if $u > 0.65$, short if $u < 0.35$, else
score 0 at residual confidence.

**KYLE (λ residual)** — rolling OLS of per-bar log returns on signed flow (Kyle 1985):

$$r_i = \lambda\,\Delta_i + \varepsilon_i, \qquad
\varepsilon_{\mathrm{cum}} = \sum_i \varepsilon_i, \qquad
z_\varepsilon = \frac{\varepsilon_{\mathrm{cum}} - \mu_{\text{path}}}{\sigma_{\text{path}}}$$

$z_\varepsilon \gg 0$ means price ran **above** what flow justifies — a fragile rally, so the score
fades it ($S = -z_\varepsilon$ clamped); $z_\varepsilon \ll 0$ with positive delta means aggressive
buying was **absorbed** without giving back — long bias. $\hat\lambda$ itself is not a direction
signal: it feeds the slippage model and the position-size damper (thin book ⇒ high impact).

### Structure signals (the triple regression-line block)

Three exponentially weighted regressions over the last $N$ bars — the **buy-volume centroid**
line, the **sell-volume centroid** line, and the **POC** (point-of-control) line. Each fit
minimises $\sum_i w_i (y_i - \alpha - \beta x_i)^2$ with decay weights
$w_i = \delta^{\,n-1-i}\cdot v_i$ (forgetting factor $\delta$, volume $v_i$), and the slope's
standard error is corrected for serial correlation with a **Newey-West (1987)** HAC estimator
(Bartlett kernel, plug-in bandwidth $L = \lfloor 4(n/100)^{2/9} \rfloor$).

**INITIATIVE** — which side's price acceptance advances faster, as a t-statistic:

$$t = \frac{\beta_b - \beta_s}{\sqrt{se_b^2 + se_s^2}}, \qquad S = \mathrm{clamp}(t, -3, 3)$$

**CONTROL** — where the POC sits inside the wedge, the *control coordinate*:

$$\rho = \frac{\hat p - \hat s}{\hat b - \hat s} \in [0, 1], \qquad
S = \mathrm{clamp}\big(6(\rho - \tfrac12) + \mathrm{sgn}(\dot\rho)\min(1, 30\,\lvert\dot\rho\rvert),\ -3,\ 3\big)$$

$\rho \to 1$: volume control is migrating toward the buyers' line (bullish); $\rho \to 0$ bearish;
the rotation $\dot\rho$ reinforces the move.

**WEDGE** — wedge width $w = \hat b - \hat s$ and its velocity $\dot w = \beta_b - \beta_s$.
Converging ($\dot w < 0$) is a coil: resolve with the POC trend,
$S = \mathrm{sgn}(\beta_{poc})\cdot\min(3, \lvert t_{poc} \rvert)$. Expanding: direction follows the
faster-advancing side, $S = (\beta_b - \beta_s)/\sigma_{res}$.

**VALUE** — deviation of price from fitted value:

$$z_p = \frac{\text{mid} - \hat p_{poc}}{\sigma_{res}}$$

When $\lvert z_p \rvert > 2$ **and** the POC trend is flat ($\lvert t_{poc} \rvert < 1.5$), fade the
stretch back toward value ($S = -z_p$). A steep, confident POC trend disables the fade — stretch in
a trend is not mispricing.

### Divergence & book signals

**CVD** — cumulative-delta divergence. Fit EW lines to price and to CVD over the same window; when
the slopes disagree in sign, fade the price:

$$S = -3\,\mathrm{sgn}(\beta_{price}) \cdot \min(R^2_{price},\ R^2_{cvd})$$

**OBI** — order-book imbalance at the touch, real depth only, invalidated past its TTL:

$$\mathrm{OBI} = \frac{Q_{bid} - Q_{ask}}{Q_{bid} + Q_{ask}}, \qquad S = 3\,\mathrm{OBI}$$

## Combination: $w = \Sigma^{-1}\cdot IC$

No hand-tuned weights. On every bar close the engine logs the 11 scores and the mid; once enough
history exists:

1. **Signal covariance** $\Sigma$ via **Ledoit-Wolf (2004)** shrinkage toward a scaled identity —
   $\hat\Sigma = \hat\delta\,\mu I + (1-\hat\delta)\,S$ with the plug-in optimal intensity
   $\hat\delta$. The blend is always PSD and well-conditioned, which the inverse below needs.
2. **Information coefficient** per signal: the **Spearman rank correlation** of each score column
   with the forward log-return $r_{t \to t+h} = \ln(p_{t+h}/p_t)$ (rank IC is robust to outliers
   and signal non-linearity).
3. **Weights** $w \propto \Sigma^{-1}\cdot IC$, L1-normalised ($\sum_i \lvert w_i \rvert = 1$),
   signs preserved — a signal whose IC says "fade it" keeps a **negative hedge weight**. This is
   the Grinold-Kahn / mean-variance optimum: high-IC signals are over-weighted, redundant
   (highly correlated) signals are down-weighted.

The composite then blends scores through three modulation layers — weight, regime multiplier,
confidence — and skips anything stale past its TTL:

$$C = \frac{\sum_i w_i\, m_i\, \text{conf}_i\, S_i}{\sum_i \lvert w_i\, m_i \rvert}$$

- $m_i$: regime multiplier (ADX + Bollinger-width classifier into TrendingBull/Bear, Ranging,
  HighVolatility), augmented by wedge state — in a **coil** the structural signals
  (initiative/control/wedge/value) get ×1.15 and momentum signals ×0.9; in expansion the reverse.
- TTLs are bar-span multiples per signal family ($\text{TTL} = \alpha \cdot \text{span}$), so
  fast-decaying signals (delta, footprint) expire quicker than structural fits.

## Calibration, entry gate, exits, sizing

**Isotonic calibration.** The raw composite is mapped onto expected forward return with
pool-adjacent-violators (PAVA) isotonic regression over logged $(C, r_{fwd})$ pairs:

$$g(C) = \mathbb{E}[\,r_{t \to t+h} \mid C\,] \quad \text{(monotone non-decreasing fit)}$$

Until the sample count and per-region support are trustworthy the engine runs in **bootstrap
mode**: $g$ is halved toward neutral and entries fall back to a fixed composite threshold
$\lvert C \rvert \ge C_{min}$. The mode is surfaced in the snapshot and as a window badge.

**Cost gate.** A trade is considered only in direction $\mathrm{sgn}(C)$ and only when the
calibrated edge clears the full round trip:

$$\lvert g(C) \rvert \cdot p_{entry} \ \ge\ \text{spread} + 2 \cdot \text{fee} + \mathbb{E}[\,\text{slip} \mid C\,]$$

Conditional slippage is a binned empirical model over $\lvert C \rvert$ (observed fills land in
their bin's running mean), falling back to a Kyle-λ linear-impact estimate — never an
unconditional average.

**Structure-anchored brackets.** Stops/targets anchor to flow structure: long stops below
$\min(\hat s,\ \hat p - \kappa\sigma_{res})$ (sell line / lower value-area edge), targets at
$\max(\hat b,\ \hat p + \kappa\sigma_{res})$ — symmetric for shorts, with σ-multiple fallbacks when
no valid structure is in range.

**First-passage EV check.** Model price as drifted Brownian motion ($\mu = g(C)\cdot p$,
$\sigma = \sigma_{bar}\cdot p$) between the stop at distance $a$ and target at distance $b$. The
win probability is the two-barrier first-passage solution with $\theta = 2\mu/\sigma^2$:

$$P(\text{target before stop}) = \frac{e^{\theta a} - 1}{e^{\theta a} - e^{-\theta b}}
\ \xrightarrow{\ \mu \to 0\ }\ \frac{a}{a+b}$$

The trade must have $\,EV = P\,b - (1-P)\,a - \text{costs} > 0$.

**Sizing.** Quarter-Kelly on the conditional edge: $f^* \approx \text{edge}/\text{odds}$
approximated as $g(C)\cdot p_{entry} / d_{stop}$, capped at the configured Kelly fraction, also
capped by a flat risk-fraction of balance, then scaled down toward ×0.5 when $\hat\lambda$ is
elevated (thin book ⇒ impact risk).

**Risk rails.** Daily-loss and max-drawdown kill switch (flattens and stops arming), UTC session
gates (Asian / London / NY / overlap), post-trade cooldown, and a time stop at a bar-span multiple.
All P&L tracking is cost-inclusive.

**Self-scaling.** All thresholds are dimensionless: price distances scale with
$\sqrt{\text{span}/\text{span}_0}$, volume thresholds are linear in median bar volume, the
footprint row size derives from the bar ATR over a target row count (snapped to the instrument
tick). Defaults target CME micro futures (MES/MNQ) on 15 s–1 m candles.

## The window

The live window (redesigned 2026-06) is built for fast reading:

- **SIGNALS chart** — every signal score plus a thicker white **Composite** overlay on one shared
  time axis; colour-matched chips above the plot toggle individual series.
- **Footprint cluster** — per-price buy/sell volume, 3:1 diagonal imbalance outlines, POC
  highlight, per-bar Δ/Σ footer with the composite-at-close marker. Rendered from the engine's own
  `FootprintBars` — exactly what the FOOTPRINT signal scored.
- **Composite gauge** — needle on a SHORT↔LONG track with the bootstrap-threshold tick marks.
- **Order book** — depth ladder that sizes to the actual level count (empty rows collapse), with
  mid/spread strip; lives in a resizable right panel above the dashboard.
- **Dashboard** — composite/direction/trade-gate, calibration ($g(C)$, conditional slippage,
  bootstrap), regime + feed quality, Kyle ($\hat\lambda$, $\varepsilon_{cum}$, $z_\varepsilon$),
  line fits (slopes + $R^2$), $\rho$/$\dot\rho$, $w$/$\dot w$, $z_p$, per-signal score/conf/TTL,
  and the live $\Sigma^{-1}\cdot IC$ weight vector (watch hedge weights go negative).
- Header badges: **Real Tape / Synthetic L1** feed quality and **BOOTSTRAP** mode.

## Project layout

| File | Role |
|---|---|
| `SigmaIcFlowStrategy.cs` | `ITradingStrategy` metadata (id, display name, data-requirement tags) |
| `SigmaIcFlowStrategyViewModel.cs` | Live VM — subscribes to the hub, owns the engine instance, exposes snapshot state |
| `SigmaIcFlowStrategyWindow.xaml(.cs)` | Window — signals chart, footprint cluster, gauge, order book, dashboard |
| `DependencyInjection.cs` | `AddSigmaIcFlowStrategy()` — registers VM, window, and `StrategyFactoryRegistration` |

## Wiring

- **Engine:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/ApexScalperStrategy.cs` —
  the `IBacktestStrategy` with all scoring/calibration logic and the UI snapshot records
  (`ApexSnapshotV2`, options in `Core/Strategies/Apex/ApexV2Options.cs`).
- **Estimators:** `src/TradingTerminal.Core/Quant/` — `EwRegression`, `NeweyWest`, `KyleResidual`,
  `LedoitWolf`, `InformationCoefficient`, `SignalWeights`, `IsotonicCalibration`, `FirstPassage`
  (pure, unit-tested, shared across strategies).
- **Live VM** extends `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`) and consumes
  `IMarketDataHub.Quotes/Bars/Depth/Trades(InstrumentId)`; the trade tape is broker-capability
  gated (IB wired).
- **DI:** `services.AddSigmaIcFlowStrategy()` from `AddStrategyPlugins()`. Opened via
  `IStrategyFactory` — the shell never references the concrete type.

## References

- Kyle, A. (1985). *Continuous Auctions and Insider Trading* — the λ price-impact regression.
- Easley, López de Prado, O'Hara (2012). *Flow Toxicity and Liquidity* — VPIN.
- Ledoit, Wolf (2004). *Honey, I Shrunk the Sample Covariance Matrix* — shrinkage Σ.
- Grinold, Kahn. *Active Portfolio Management* — IC and the $\Sigma^{-1}\cdot IC$ blend.
- Newey, West (1987) — HAC standard errors.
