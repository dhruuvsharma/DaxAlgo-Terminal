# Methods & math reference

> Last updated: 2026-06-30

A single place that writes down **the actual math** behind every strategy, analytical tool, and chart in the terminal — the formula, the variable meanings, and a pointer to the source file that implements it. It exists so a contributor or researcher can understand *what is being computed* without reverse-engineering the code.

Everything here is grounded in shipped code. Where a strategy already carries a full derivation in its own project README (the Σ⁻¹·IC optimizer), this page summarises and links rather than duplicates.

**Conventions.** Returns are log returns $r_t = \ln(p_t/p_{t-1})$ unless stated. $\hat S(z)=\mathrm{clamp}(3z/\theta,-3,3)$ is the shared "z-score → bounded score" squash. All variance/mean accumulation uses single-pass Welford internally (never $\sum x^2 - (\sum x)^2/n$). Estimators emit a **neutral** value, never `NaN`, on a degenerate window. **Data/signals only — no formula here sizes a real order.**

---

## 0. How to read this page (plain-language primer)

You do **not** need a maths degree. Almost every formula below is built from the same handful of
ideas. Learn these eight and the rest of the page decodes itself. Each comes with a one-line meaning
and a tiny worked number.

### Average (mean) — "the typical value"

Add the numbers up, divide by how many there are. If the last five trade sizes were 2, 4, 4, 6, 9
contracts, the average is $(2+4+4+6+9)/5 = 5$. Written $\bar x$ ("x-bar").

### Standard deviation ($\sigma$) — "how spread out the numbers are"

Roughly, the *typical distance* of a value from the average. Small $\sigma$ = everything clusters
near the mean; large $\sigma$ = wildly scattered. For the sizes above, the deviations from 5 are
$-3,-1,-1,+1,+4$; squaring, averaging and square-rooting gives $\sigma \approx 2.4$. So a "normal"
trade is about 2.4 contracts away from the average of 5.

### Z-score ($z$) — "how unusual is this value?"

$$z = \frac{x - \bar x}{\sigma}$$

How many standard deviations a value sits above (+) or below (−) the average. A size of 9 has
$z = (9-5)/2.4 \approx 1.7$ — "about 1.7 standard deviations bigger than normal", i.e. somewhat
unusual but not extreme. A z near 0 is ordinary; |z| above ~2 is genuinely rare (top/bottom ~2%).
**This is the single most-used quantity on the page** — almost every "signal" is "how unusual is the
current reading versus its recent history?".

### The squash $\hat S(z)$ — "turn an unusualness score into a capped −3…+3 score"

$$\hat S(z) = \mathrm{clamp}\!\left(\tfrac{3z}{\theta}, -3, +3\right)$$

A z-score can in principle be huge; we don't want one freak reading to dominate a blend. The squash
rescales by a threshold $\theta$ (often 2) and then *clamps* (hard-limits) the result to the range
$[-3, +3]$. Worked: $z = 2,\ \theta = 2 \Rightarrow 3\cdot 2/2 = 3$ (maxed out). $z = 0.5 \Rightarrow
0.75$. $z = 10 \Rightarrow$ still just $+3$. Every strategy signal in this app speaks this common
−3…+3 "language" so they can be compared and combined fairly.

### Log return — "percentage change that adds up cleanly"

$$r_t = \ln\!\left(\frac{p_t}{p_{t-1}}\right)$$

The change in price from one bar to the next, expressed so that gains and losses *sum* over time
(ordinary percentages don't: +10% then −10% is not 0%). For small moves it's almost identical to the
percentage change — a move from 100 to 101 is $\ln(101/100) \approx 0.995\% \approx 1\%$.

### Correlation ($\rho$) — "do two things move together?"

A number from $-1$ to $+1$. $+1$ = they rise and fall in perfect lockstep; $0$ = unrelated; $-1$ =
perfect mirror images. Used to tell whether two instruments (or two trading signals) are really
giving you independent information or just saying the same thing twice.

### Regression slope ($\beta$) — "the steepness of the best-fit trend line"

Draw the straight line that best fits a cloud of points; $\beta$ is how steeply it rises. A positive
$\beta$ on a price-vs-time fit means an uptrend; the *standard error* of $\beta$ tells you how
trustworthy that slope is (a steep line through scattered points is less reliable than a steep line
through tight points).

### Covariance matrix $\Sigma$ and its inverse $\Sigma^{-1}$ — "the redundancy table"

When you have several signals, $\Sigma$ is a grid recording how each pair moves together. Its
**inverse** $\Sigma^{-1}$ is the mathematical tool that says "two signals that always agree are
partly redundant — don't count both at full strength." It's what stops a blend from being fooled by
five copies of the same idea. Paired with the **Information Coefficient (IC)** — how well a signal's
ranking has predicted future returns — the product $\Sigma^{-1}\!\cdot IC$ gives the *optimal* mix:
reward signals that predict well, discount signals that merely echo each other.

> **Throughout the page:** a hat ( $\hat{}$ ) means "an estimate of"; $\sum$ means "add up";
> $\mathrm{clamp}(x,a,b)$ means "if $x<a$ use $a$, if $x>b$ use $b$, else use $x$"; $\Delta$ ("delta")
> means "the change in". Anywhere you see a z-score, mentally read "how unusual, in std-devs."

---

## 1. Shared building blocks

These pure modules are reused across strategies, tools and charts — learn them once.

### 1.1 Indicators — `Core/MarketData/Indicators.cs`

| Indicator | Formula | Notes |
|---|---|---|
| SMA($n$) | $\frac1n\sum_{i=0}^{n-1} p_{t-i}$ | simple rolling mean |
| EMA($n$) | $e_t = \alpha p_t + (1-\alpha)e_{t-1}$, $\alpha = \tfrac{2}{n+1}$ | seeded with the first SMA |
| Wilder RSI($n$) | $100 - \dfrac{100}{1+RS}$, $RS=\dfrac{\overline{\text{gain}}}{\overline{\text{loss}}}$ | gains/losses smoothed with Wilder's $\alpha=1/n$ |
| ATR($n$) | Wilder average of $\text{TR}_t=\max(h-l,\,|h-c_{t-1}|,\,|l-c_{t-1}|)$ | true-range volatility |
| Rolling stdev | Welford over the window | guard $\sigma>0$ before any z-score |

### 1.2 Microstructure — `Core/MarketData/Microstructure.cs`

| Quantity | Formula | Range |
|---|---|---|
| **Microprice** | $\dfrac{Q_b\,a + Q_a\,b}{Q_b+Q_a}$ | leans to the thinner side; falls back to mid when sizes are 0 |
| **Queue imbalance** | $\dfrac{Q_b - Q_a}{Q_b + Q_a}$ | $[-1,1]$ |
| **Cumulative (L2) imbalance** | $\dfrac{\sum_{i\le N} Q_{b,i} - \sum_{i\le N} Q_{a,i}}{\sum Q_{b,i}+\sum Q_{a,i}}$ | $[-1,1]$ over top $N$ levels |
| **Weighted mid** | $\dfrac{\sum (Q_{b,i}p_{b,i} + Q_{a,i}p_{a,i})}{\sum (Q_{b,i}+Q_{a,i})}$ | size-weighted across the book |
| **Half-spread** | $(a-b)/2$ | price units |
| **Estimated slippage** | sweep $q$ through the side, $\lvert \bar p_{\text{fill}} - p_{\text{touch}}\rvert$ | walks levels in order |
| **Largest level gap** | $\max_i \lvert p_i - p_{i-1}\rvert$ | thin-book detector |

**Lee–Ready (1991) aggressor classification.** Brokers that don't report the initiating side are signed by: (1) *quote rule* — print $\ge$ ask ⇒ **buy**, $\le$ bid ⇒ **sell**; (2) *tick rule* inside the spread — higher than prior print ⇒ buy, lower ⇒ sell, equal ⇒ carry the prior class forward (zero-tick). First ambiguous print of a session returns `Unknown` rather than guessing.

> **Worked example — microprice & queue imbalance.** Say the best bid is 100.00 with **800** lots
> waiting, and the best ask is 100.25 with **200** lots. The naive **mid** is $(100.00+100.25)/2 =
> 100.125$. But there is 4× more size resting on the bid, so:
> - **Queue imbalance** $= (800-200)/(800+200) = +0.6$ — leaning bullish (buyers outweigh sellers in
>   the queue).
> - **Microprice** $= (Q_b\,a + Q_a\,b)/(Q_b+Q_a) = (800\cdot100.25 + 200\cdot100.00)/1000 = 100.20$ —
>   pulled *up*, toward the thin ask, because the heavy bid is likely to absorb selling and the next
>   print "wants" to be nearer the ask. Microprice is a better short-horizon fair value than the mid
>   precisely because it accounts for this lopsidedness.

### 1.3 Trade-flow imbalance OBI(T) — `Core/MarketData/OrderFlowImbalance.cs`

Trade-based order-book imbalance over a backward window (Anantha–Jain–Maiti 2025, Eq. 17):

$$\mathrm{OBI}(T) = \frac{N_{\text{buy}} - N_{\text{sell}}}{N_{\text{buy}} + N_{\text{sell}}} \in [-1,1]$$

Classified into **9 equal-width regimes** across $[-1,1]$, re-centred to $-4..+4$: bin $=\mathrm{clamp}\big(\lfloor (\mathrm{OBI}+1)/w\rfloor,0,8\big)-4$ with $w=2/9$. $|{\text{regime}}|\ge k$ is a "strong" regime.

> **In plain terms.** Count how many recent trades hit the *ask* (buyers in a hurry) versus the
> *bid* (sellers in a hurry); OBI(T) is just "buyers minus sellers, as a fraction." **Worked
> example:** of the last 50 trades, 35 were buyer-initiated and 15 seller-initiated, so $\mathrm{OBI}
> = (35-15)/50 = +0.4$. Dropping it into the 9-bin map: $w = 2/9 \approx 0.222$, bin
> $= \lfloor (0.4+1)/0.222 \rfloor - 4 = \lfloor 6.3 \rfloor - 4 = +2$ — a *moderate* buy-pressure
> regime. With a "strong" cut-off of $k=3$, this $+2$ would **not** yet be strong enough to act on.

### 1.4 Quant estimators — `Core/Quant/`

Pure, unit-tested, shared across the composite strategies.

| Module | Computes | Canonical form |
|---|---|---|
| `EwRegression` | exponentially-weighted OLS line | minimise $\sum_i w_i(y_i-\alpha-\beta x_i)^2$, $w_i=\delta^{\,n-1-i}v_i$ |
| `NeweyWest` | HAC slope standard error | Bartlett kernel, bandwidth $L=\lfloor 4(n/100)^{2/9}\rfloor$ |
| `KyleResidual` | price-impact $\hat\lambda$ + residual z | $r_i=\lambda\Delta_i+\varepsilon_i$ (Kyle 1985); v2 uses 2SLS IV (instrument $\Delta_{t-1}$) |
| `LedoitWolf` | shrunk covariance $\hat\Sigma$ | $\hat\delta\mu I+(1-\hat\delta)S$, plug-in optimal $\hat\delta$ — always PSD |
| `InformationCoefficient` | per-signal IC | Spearman rank corr of score vs forward return |
| `SignalWeights` | mean-variance weights | $w\propto\Sigma^{-1}\cdot IC$, L1-normalised, signs preserved (Grinold–Kahn) |
| `IsotonicCalibration` | $g(C)=\mathbb E[r\mid C]$ | pool-adjacent-violators (PAVA), monotone non-decreasing |
| `FirstPassage` | two-barrier win prob | $P=\dfrac{e^{\theta a}-1}{e^{\theta a}-e^{-\theta b}}$, $\theta=2\mu/\sigma^2 \xrightarrow{\mu\to0} \dfrac{a}{a+b}$ |
| `HawkesProcess` | self-exciting intensity | $\lambda(t)=\mu+\sum_{t_i<t}\alpha e^{-\beta(t-t_i)}$ |
| `KalmanPocPredictor` | constant-velocity node forecast | Kalman filter on (price, velocity); confidence $1-\sigma^2_{\text{pred}}/\sigma^2_{\text{bar}}$ |
| `CurveFitting` | the chart fit family | OLS / quadratic / cubic / Theil–Sen / exp / log / LOWESS |
| `DeflatedSharpe` | multiple-testing-adjusted Sharpe | Bailey–López de Prado deflation |

### 1.5 Time-series — `Core/Quant/TimeSeries/` (Machine-Learning menu)

`Ols`, `ArimaModel` (AR/I/MA, fit by `NelderMead` on conditional SSE), `GarchModel` (GARCH(1,1) variance $\sigma^2_t=\omega+\alpha\varepsilon^2_{t-1}+\beta\sigma^2_{t-1}$), `KalmanFilters`, `StationarityTests` (ADF, KPSS, ACF/PACF), `SeriesTransforms` (differencing, log). Full write-up in [machine-learning.md](machine-learning.md).

---

## 2. Strategy math

Each strategy has an engine-side `IBacktestStrategy` (`Infrastructure/Backtest/Strategies/`) and a live VM that wraps it. The catalog one-liners live in [strategies.md](strategies.md); the math is here.

### Demos (engine-only)

- **`buyAndHold`** — market-buy on the first tick, sell on the last. Smoke test.
- **`meanReversion`** — rolling mean $\bar p$ over $n$ ticks; buy when $p \le \bar p - \tau$, exit at $\bar p$, stop at $\bar p - \tau_{\text{stop}}$; symmetric short. Fixed thresholds, not a z-score.
- **`donchianBreakout`** — go long when ask $>$ highest bid of the prior $n$ ticks (Donchian upper channel); trailing stop a fixed distance behind the best mid since entry; symmetric short.

### Cumulative delta — CumulativeDelta (live-only)

Cumulative signed volume $\text{CVD}_t=\sum_{i\le t}\mathrm{sgn}_i\,v_i$ (Lee–Ready sign). The **slope and price-divergence** of CVD are the signal, not its drifting level; footprint clusters give the price-acceptance context.

### Order-Flow Cube (3D) — `orderFlowCube`

Three flow axes over a rolling trade window vs a longer baseline:

$$\text{CVD imb}=\frac{V_{\text{buy}}-V_{\text{sell}}}{V_{\text{tot}}},\quad \text{aggressor}=\frac{V_{\text{buy}}}{V_{\text{tot}}},\quad \text{size ratio}=\frac{\bar v_{\text{window}}}{\bar v_{\text{baseline}}}$$

The 8-octant cube trades the two clearest octants: **accumulation** (long) when all three clear their thresholds (CVD$\ge$, aggressor$\ge$, size$\ge$), **distribution** (short) mirrored. Exit on CVD regime reversal or a `HoldTrades` time-stop. (Note in code: CVD imb $\approx 2\cdot$aggressor$-1$ on the same window, so run the axes over *different* windows for true orthogonality.)

### Order-Flow Surface Spike (3D) — `orderFlowSurfaceSpike`

Rolling matrix of signed volume over [`NumSlices` time slices × price bins], price binned by $\lfloor p/\text{binSize}\rfloor$. Z-score the **whole** surface, $z=(v-\bar v)/\sigma$ over all cells, and find the highest-$|z|$ cell in the *latest* slice. A spike with $|z|\ge$ threshold, confirmed for `ConfirmationTicks` consecutive same-direction breaches, enters with the spike ($z>0$ ⇒ long). Exit on fixed TP/SL in price units, spike dissipation, or sign flip.

### Imbalance Heat Front (3D) — `imbalanceHeatFront`

Rolling matrix of per-distance L2 imbalance ratios. Detect a **ridge** — $\ge$ `RidgeWidth` consecutive distance levels with $|\text{imbalance}|\ge$ `RidgeThreshold`, persisting `ConfirmationSlices` samples on one side. **Momentum** mode trades with the ridge (bids dominate ⇒ long); **MeanReversion** mode fades it (one-sided books exhaust). Exit on dissolution, sign flip, or TP/SL. *Backtest caveat:* the engine is L1-only, so ridge detection degenerates to a single touch-level check; the live window uses real L2 depth.

### Index K-Score Surface (3D) — `indexKScoreSurface`

Aggregates ticks into fixed-interval bars and computes a **15-indicator K-score** $K\in[-1,1]$ per bar close (`Core/IndexKScore/`); enter in the direction of $K$ when $|K|\ge$ `EntryThreshold`, exit at `ExitThreshold`. The full strategy weights each index constituent's K-score by membership and aggregates cross-sectionally (live multi-stock VM); the engine variant is the single-instrument sanity check.

### Index Regime Graph — `index.regime.graph` (live-only)

Runs the Advanced Market Regime indicator stack (§3.4) across every index constituent, blends each stock's 8 timeframes for the chosen horizon, weights by index membership, and sums to a composite direction — rendered as a pan/zoom node graph.

### Filtered Order-Flow Imbalance — `filtered.orderflow.imbalance` (research paper)

Anantha–Jain–Maiti 2025 ([arXiv:2507.22712](https://arxiv.org/abs/2507.22712)). Maintains both **unfiltered** OBI(T) and a **filtered** OBI(T) that drops sub-threshold / fleeting prints (the tape-level analog of the paper's parent-order filtration). When the filtered OBI enters a strong 9-bin regime ($|{\text{regime}}|\ge$ `StrongRegime`), take a **same-sign** position; exit after `HoldSeconds` of event time or when the regime decays through neutral. Both series are surfaced so the paper's central claim — filtration sharpens the directional signal — is observable live.

### 1-Minute Order-Flow Pressure Map — `orderflow.pressuremap` (monitor)

Multi-ticker S&P 100/500 grid (ticker × time). Per cell flags **unusual 1-minute volume** via a z-score of current-minute volume against the rolling per-ticker baseline, and separates **absorption** (high volume, little price move) from **breakthrough** (high volume with displacement) using the volume-to-range ratio.

### Σ⁻¹·IC Order-Flow Optimizer — `sigmaIcFlow`

The flagship composite: 12 microstructure signals fused with mean-variance optimal weights $w\propto\Sigma^{-1}\cdot IC$ (Ledoit–Wolf $\Sigma$, Spearman IC), isotonic calibration $g(C)=\mathbb E[r\mid C]$, a full round-trip cost gate, a first-passage EV check, and ¼-Kelly sizing. Every formula — DELTA / VPIN / FOOTPRINT / TAPE_SPEED (Hawkes) / KYLE (2SLS) / the triple EW-regression structure block with Newey–West errors / CVD / OBI / PRED_NODE (Kalman) — is derived in the **[project README](../src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/README.md)**. Don't duplicate it; read it there.

---

## 3. Tool math

### 3.1 Correlation matrix — `TradingTerminal.Correlation`

- **Pearson** $\rho=\mathrm{cov}(x,y)/(\sigma_x\sigma_y)$, covariance via single-pass Welford (no catastrophic cancellation on large prices).
- **Spearman** = Pearson on ranks — preferred for fat-tailed returns.
- **Live** matrix uses **EWMA covariance** (RiskMetrics) $\Sigma_t=\lambda\Sigma_{t-1}+(1-\lambda)r_tr_t^{\!\top}$, $\lambda\approx0.94$, for responsiveness.
- **PSD repair** before any decomposition: clip negative eigenvalues to 0, renormalise the diagonal.
- **PCA** eigendecomposes the correlation matrix; PC1 loadings ≈ the market factor, explained-variance ratio $\lambda_i/\sum\lambda$.

### 3.2 Market regime composite — `Core/Regime/MarketRegimeCalculator`

A 0–100 risk-on/off score from **ten** sub-signals (volatility, positioning, trend, breadth, momentum, credit, liquidity, macro, sentiment, cross-asset). Each raw input is mapped to a 0–100 sub-score (e.g. VIX and price-vs-200dma are normalised against their own historical range, sentiment surveys onto bull/bear balance), then the composite is the **weighted mean** of the available categories — a failed source drops out and the weights renormalise rather than poisoning the score. Bands: 0–24 Extreme Fear … 75–100 Extreme Greed (`RegimeStateMapper`). It's a risk-management input, not a standalone signal.

### 3.3 Advanced market regime board — `Core/MarketData/AdvancedRegime/`

A multi-timeframe indicator board: **18 rows** (RSI, MACD, CCI, MA 9/21/50, 3-MA stack, VWAP, SuperTrend, ATR, ATR-regression, STD, POC, TRD, delta, cumulative delta, volume buy/sell, and a composite **Trend** needle) across **8 timeframe columns** (1m…1D, with aggregated 20m/30m buckets via `BarTimeframeAggregator`, not broker `BarSize` requests). Each cell is classified bullish / bearish / neutral from its indicator's standard rule (e.g. RSI vs 30/70, MACD histogram sign, price vs SuperTrend); the Trend needle sums the cell votes. The **Index Regime Graph** strategy runs this stack across every index constituent.

---

## 4. Chart math

### 4.1 Volume footprint — `TradingTerminal.VolumeFootprint`

- **Bars** built by `FootprintFeatures` (Core): per (time bucket, price bucket) cell of buy vs sell volume; **POC** is the price row of max total volume per column.
- **Stacked-imbalance** (the diagonal rule): a bid/ask cell pair is "stacked" when one side exceeds the diagonal-opposite side by the **3:1** ratio; consecutive stacked levels mark absorption fronts (same rule the Σ⁻¹·IC FOOTPRINT signal scores).
- **POC slopes** in the stats panel and the seven fit curves come from `CurveFitting` (`Core/Quant/`): OLS (linear/quadratic/cubic), **Theil–Sen** (median of pairwise slopes — robust to outlier POC bars), exponential (log-space OLS), logarithmic $a+b\ln(x+1)$, and **LOWESS** (locally-weighted linear, tricube kernel, half-sample span). The **virtual predictor** extrapolates each enabled fit $N$ bars out and draws their mean as the consensus. Per-project [README](../src/windows/Charts/TradingTerminal.VolumeFootprint/README.md) has the exact forms.
- **ML predictor** (`Core/Ml/FootprintNextBarPredictor`) — an online multi-output forecaster: a bank of **RLS learners with exponential forgetting** ($\lambda=0.995$, §1.4-style recursive update via `OnlineLinearRegression`), one per (target × horizon), 5 targets × 8 **direct** horizons (learner$_h$ fits $y(t+h)-\text{ref}(t)$; no recursive iteration, no compounding). Targets are stationary by construction: POC/buy-POC/sell-POC deltas in ticks, $\ln(1+v_{t+h})-\overline{\ln v}_t$, and volume-normalised delta clamped ±3 (EWMAs half-life 16 bars, snapshotted at prediction time). 16 engineered features (POC lags, delta ratio + EWMA, 3-bar CVD change, value-area width, bar range, stacked-run imbalance, relative log-volume, `FeedQuality` multiplier, and the regression consensus as a **meta-feature** with validity flag) pass through an exponentially-weighted **online standardiser** (Welford-with-decay, clip ±5) before both `Predict` and `Update`. Walk-forward scoring: rolling MAE (ticks) + directional hit-rate over 100 scores, computed for the model *and* the regression-consensus baseline on identical realized bars; a warm-start replays ≤ interval×200 bars (≤ 24 h) of stored tape through the shared `FootprintTimeBucketer` before live data.

### 4.2 Bookmap + VolBook — `TradingTerminal.Heatmap`

- **Liquidity heatmap** — resting L2 size per (price, time) cell on a magma ramp, **√-compressed** ($\sqrt{\text{size}}$) so large levels don't wash out small ones.
- **VWAP** — developing session $\dfrac{\sum p\,v}{\sum v}$.
- **CVD panel** — cumulative volume delta $\sum(\text{buy}-\text{sell})$ over per-column net-delta bars.
- **Session volume profile** — volume-at-price histogram; **POC** = max-volume bucket; **70% value area** (VAH/VAL) grown outward from the POC until 70% of session volume is enclosed.
- **Large-lot / iceberg** detection — a print is a large lot at $\ge 5\times$ the rolling-mean trade size; an iceberg is the same price+size **refilling $\ge 4\times$**.

### 4.3 Order book — `TradingTerminal.OrderBook`

Live L2 ladder; per-level bars normalised to the largest level on either side; **cumulative depth** per level $\sum_{j\le i} Q_j$. Read-outs: best bid/ask, spread $a-b$, mid $(a+b)/2$. The strip adds the `Microstructure` (Core) estimators: **microprice** $\frac{Q_b\,a + Q_a\,b}{Q_b+Q_a}$, **queue imbalance** $\frac{Q_b-Q_a}{Q_b+Q_a}$ (L1 and cumulative top-10), size-weighted mid, book skew, and **sweep cost** (average fill price minus the touch, walking the levels for a chosen size).

- **ML micro-forecaster** (`Core/Ml/OrderBookMicroPredictor`) — online multi-output model stepped once per 250 ms capture tick: **direction bank** of RLS learners with exponential forgetting ($\lambda=0.995$), one per **direct** horizon $h \in \{1,2,4,8,20\}$ steps, fitting microprice change in ticks $y_h = (\mu_{t+h}-\mu_t)/\tau$ (no recursive iteration ⇒ no compounding); **event bank** of 3 linear-probability learners (outputs clamped [0,1]) over one $W=8$-step window, labels from `OrderBookEventLabeler`: spread widened ($\max$ spread $\ge$ ref + 1 tick), depth drained (either side's top-3 depth $\le 0.7\times$ ref), sweep jumped (worst-side sweep $\ge 1.25\times$ ref, one-tick floor). 19 features (lagged $\Delta\mu$, L1/cum imbalance, $\mu-$mid, spread, log-depth skew $\ln\frac{1+Q_b}{1+Q_a}$, depth deltas vs EWMA, level gaps, sweep costs, normalized signed flow + EWMA, trade intensity, flow-validity flag) through an EW-Welford **online standardiser** (clip ±5). Tick size is a running min of positive adjacent level gaps; pendings pin their creation tick. Scoring: flagship 1 s MAE/hit-rate vs the **queue-imbalance baseline** $\hat y = \operatorname{sign}(\text{imb}_{L1})\cdot\frac{s}{2\tau}$ (dead-band 0.05) on identical realized steps; events by rolling **Brier score** $\overline{(p-y)^2}$ vs the window base rate. Warm-start replays stored depth through `DepthStepSampler` (LOCF onto the 250 ms grid, >5 s gaps skipped).

### 4.4 Charts (TradingView-style) — `TradingTerminal.Charts`

Candles render in Lightweight Charts (WebView2) but **every overlay number is computed in C#** by the Core `Indicators` (§1.1) — SMA(20), EMA(50), RSI(14), MACD(12/26/9) — so chart, backtest, and live values agree exactly.

### 4.5 3D Surface lab — `TradingTerminal.SurfaceLab`

Math in `Core/Quant/Surfaces/` (tests: `SurfaceLabMathTests`). One pass buckets simple returns $r_t = c_t/c_{t-1} - 1$ (cross-sectional mode buckets the **t+1** return conditioned on variables at $t$) into a grid capped at 81 points/axis; a second pass evaluates the chosen statistic per cell. Empty cells are NaN (rendered as holes).

- **Bucketing**: prior-return bins $\lfloor (r-\min)/\text{step} \rfloor$; volatility deciles over a 20-bar rolling $\sigma$; volume deciles; time-lag axes condition the other variable at $t-k$. Deciles use cut points at $b\cdot n/10$, binary-searched.
- **Statistics** (`SurfaceMetricRegistry`, 16, all NaN-safe): mean/median/sample-σ/count/P(>0); **annualized vol** $\sigma\sqrt{P}$ with $P$ = periods/year inferred from the median bar spacing (correct for 24/7 crypto and session equities alike); **historical VaR**$_c$ = $-r_{(\lfloor(1-c)n\rfloor)}$ and **CVaR/ES**$_c$ = mean of the worst $\lfloor(1-c)n\rfloor$ returns (sign-flipped); skew $m_3/m_2^{3/2}$, excess kurtosis $m_4/m_2^2-3$; z-score $\bar r/(\sigma/\sqrt n)$ with $\varphi(z)$ / $\Phi(z)$ (Abramowitz–Stegun 7.1.26 erf, $|\varepsilon|<1.5\times10^{-7}$); lag-1 autocorrelation $\sum d_i d_{i-1}/\sum d_i^2$; **Amihud (2002) illiquidity** $\overline{|r|/(\text{close}\cdot\text{vol})}\times10^6$.
- **Formula bar** (`SurfaceFormula`): recursive-descent grammar over metric-id variables — `+ − * / ^` (`^` right-assoc, tighter than unary minus), `Log/Exp/Sqrt/Abs`, variadic `Max/Min/Avg/Sum`; parsed once, evaluated per cell with metric caching.
- **Robustness** per cell = $\operatorname{clamp}\!\left(3\sqrt{\overline{\left(\frac{z-z_{\text{nbr}}}{z_{\max}-z_{\min}}\right)^2}},\,0,\,1\right)$ over the ≤ 8 neighbours — 0 = plateau (stable), 1 = isolated spike (overfit).
- **Live mode**: `LiveBarSeries` (rolling, hard-capped) is fed quote mids + trade sizes through bounded DropOldest channels; a coalesced 1 s timer rebuilds the grid off-thread only when dirty.

---

## References

- Kyle, A. (1985). *Continuous Auctions and Insider Trading.*
- Lee, Ready (1991). *Inferring Trade Direction from Intraday Data.*
- Easley, López de Prado, O'Hara (2012). *Flow Toxicity and Liquidity in a High-Frequency World* (VPIN).
- Ledoit, Wolf (2004). *Honey, I Shrunk the Sample Covariance Matrix.*
- Grinold, Kahn. *Active Portfolio Management* (IC and the $\Sigma^{-1}\cdot IC$ blend).
- Newey, West (1987). *A Simple, Positive Semi-Definite, Heteroskedasticity- and Autocorrelation-Consistent Covariance Matrix.*
- Asness, Moskowitz, Pedersen (2013). *Value and Momentum Everywhere* (vol targeting).
- Anantha, Jain, Maiti (2025). *Order-Flow Filtration and Directional Association with Short-Horizon Returns* ([arXiv:2507.22712](https://arxiv.org/abs/2507.22712)).
- Bailey, López de Prado (2014). *The Deflated Sharpe Ratio.*
