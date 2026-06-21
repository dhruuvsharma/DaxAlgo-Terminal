# Methods & math reference

> Last updated: 2026-06-20

A single place that writes down **the actual math** behind every strategy, analytical tool, and chart in the terminal — the formula, the variable meanings, and a pointer to the source file that implements it. It exists so a contributor or researcher can understand *what is being computed* without reverse-engineering the code.

Everything here is grounded in shipped code. Where a strategy already carries a full derivation in its own project README (the Σ⁻¹·IC optimizer), this page summarises and links rather than duplicates.

**Conventions.** Returns are log returns $r_t = \ln(p_t/p_{t-1})$ unless stated. $\hat S(z)=\mathrm{clamp}(3z/\theta,-3,3)$ is the shared "z-score → bounded score" squash. All variance/mean accumulation uses single-pass Welford internally (never $\sum x^2 - (\sum x)^2/n$). Estimators emit a **neutral** value, never `NaN`, on a degenerate window. **Data/signals only — no formula here sizes a real order.**

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

### 1.3 Trade-flow imbalance OBI(T) — `Core/MarketData/OrderFlowImbalance.cs`

Trade-based order-book imbalance over a backward window (Anantha–Jain–Maiti 2025, Eq. 17):

$$\mathrm{OBI}(T) = \frac{N_{\text{buy}} - N_{\text{sell}}}{N_{\text{buy}} + N_{\text{sell}}} \in [-1,1]$$

Classified into **9 equal-width regimes** across $[-1,1]$, re-centred to $-4..+4$: bin $=\mathrm{clamp}\big(\lfloor (\mathrm{OBI}+1)/w\rfloor,0,8\big)-4$ with $w=2/9$. $|{\text{regime}}|\ge k$ is a "strong" regime.

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

### Ornstein–Uhlenbeck — `ornsteinUhlenbeck`

Model $dX_t=\theta(\mu-X_t)\,dt+\sigma\,dW_t$, discretised as AR(1) $X_{t+1}=a+bX_t+\varepsilon$. Rolling-window OLS over the last `Lookback` mids, refit every `RefitEvery` ticks:

$$b=\widehat{e^{-\theta\Delta t}},\quad \mu=\frac{a}{1-b},\quad \sigma^2_{\text{stat}}=\frac{\mathrm{Var}(\varepsilon)}{1-b^2}$$

Trade the stationary z-score $z=(X_t-\hat\mu)/\hat\sigma_{\text{stat}}$: enter long at $z\le-Z_{\text{entry}}$, short at $z\ge Z_{\text{entry}}$, flatten at $|z|\le Z_{\text{exit}}$, stop at $|z|\ge Z_{\text{stop}}$. **Fit rejected** if $b\le0$ or $b\ge1$ (non-stationary — no mean reversion). Half-life $t_{1/2}=\ln2/\theta$ is the headline readout.

### Volatility-targeted — `volTarget`

EWMA variance of returns $\sigma^2_t=(1-\alpha)\sigma^2_{t-1}+\alpha r_t^2$ with $\alpha=1-e^{-\ln2/H}$ (half-life $H$). Position $=\mathrm{clamp}\big(\mathrm{round}(\text{TargetVol}/\sigma_t),\,1,\,Q_{\max}\big)$ — exposure shrinks when vol spikes, grows when it compresses (AQR-style risk-parity overlay; Asness–Moskowitz–Pedersen 2013). Long-only as shipped; rebalances every $k$ ticks.

### Order-flow toxicity (VPIN) — `vpin`

Volume-synchronised probability of informed trading (Easley–López de Prado–O'Hara 2012). Signed flow per tick $f = \mathrm{dir}\cdot(Q_b+Q_a)$ with $\mathrm{dir}=\mathrm{sgn}(\Delta\text{mid})$; over a rolling window:

$$\text{toxicity}=\frac{\big|\sum f\big|}{\sum |f|}\in[0,1]$$

When toxicity $\ge$ threshold, **fade** the prevailing aggressor (buyers in control ⇒ short), hold for `HoldTicks`. The engine ships the textbook **L1 tick-rule approximation**; true volume-bucket VPIN with real prints runs inside the Σ⁻¹·IC engine (§ below).

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

The flagship composite: 12 microstructure signals fused with mean-variance optimal weights $w\propto\Sigma^{-1}\cdot IC$ (Ledoit–Wolf $\Sigma$, Spearman IC), isotonic calibration $g(C)=\mathbb E[r\mid C]$, a full round-trip cost gate, a first-passage EV check, and ¼-Kelly sizing. Every formula — DELTA / VPIN / FOOTPRINT / TAPE_SPEED (Hawkes) / KYLE (2SLS) / the triple EW-regression structure block with Newey–West errors / CVD / OBI / PRED_NODE (Kalman) — is derived in the **[project README](../src/TradingTerminal.Strategies.SigmaIcFlow/README.md)**. Don't duplicate it; read it there.

---

## 3. Tool math

### 3.1 Correlation matrix — `TradingTerminal.Correlation`

- **Pearson** $\rho=\mathrm{cov}(x,y)/(\sigma_x\sigma_y)$, covariance via single-pass Welford (no catastrophic cancellation on large prices).
- **Spearman** = Pearson on ranks — preferred for fat-tailed returns.
- **Live** matrix uses **EWMA covariance** (RiskMetrics) $\Sigma_t=\lambda\Sigma_{t-1}+(1-\lambda)r_tr_t^{\!\top}$, $\lambda\approx0.94$, for responsiveness.
- **PSD repair** before any decomposition: clip negative eigenvalues to 0, renormalise the diagonal.
- **PCA** eigendecomposes the correlation matrix; PC1 loadings ≈ the market factor, explained-variance ratio $\lambda_i/\sum\lambda$.

### 3.2 Markov regime — `TradingTerminal.MarkovRegime`

Discretise into states, then the maximum-likelihood **transition matrix** with Laplace smoothing:

$$P_{ij}=\frac{\text{count}(i\to j)+\alpha}{\text{count}(i\to\cdot)+\alpha K},\qquad \sum_j P_{ij}=1$$

The **stationary distribution** $\pi$ solves $\pi P=\pi$ (left eigenvector for eigenvalue 1, normalised) — the long-run regime occupancy. HMM inference (forward / Viterbi) runs in log space to avoid underflow.

### 3.3 Market regime composite — `Core/Regime/MarketRegimeCalculator`

A 0–100 risk-on/off score from **ten** sub-signals (volatility, positioning, trend, breadth, momentum, credit, liquidity, macro, sentiment, cross-asset). Each raw input is mapped to a 0–100 sub-score (e.g. VIX and price-vs-200dma are normalised against their own historical range, sentiment surveys onto bull/bear balance), then the composite is the **weighted mean** of the available categories — a failed source drops out and the weights renormalise rather than poisoning the score. Bands: 0–24 Extreme Fear … 75–100 Extreme Greed (`RegimeStateMapper`). It's a risk-management input, not a standalone signal.

### 3.4 Advanced / Instrument regime — `Core/MarketData/AdvancedRegime/`

A multi-timeframe indicator board: **18 rows** (RSI, MACD, CCI, MA 9/21/50, 3-MA stack, VWAP, SuperTrend, ATR, ATR-regression, STD, POC, TRD, delta, cumulative delta, volume buy/sell, and a composite **Trend** needle) across **8 timeframe columns** (1m…1D, with aggregated 20m/30m buckets via `BarTimeframeAggregator`, not broker `BarSize` requests). Each cell is classified bullish / bearish / neutral from its indicator's standard rule (e.g. RSI vs 30/70, MACD histogram sign, price vs SuperTrend); the Trend needle sums the cell votes. The **Index Regime Graph** strategy runs this stack across every index constituent.

---

## 4. Chart math

### 4.1 Volume footprint — `TradingTerminal.VolumeFootprint`

- **Bars** built by `FootprintFeatures` (Core): per (time bucket, price bucket) cell of buy vs sell volume; **POC** is the price row of max total volume per column.
- **Stacked-imbalance** (the diagonal rule): a bid/ask cell pair is "stacked" when one side exceeds the diagonal-opposite side by the **3:1** ratio; consecutive stacked levels mark absorption fronts (same rule the Σ⁻¹·IC FOOTPRINT signal scores).
- **POC slopes** in the stats panel and the seven fit curves come from `CurveFitting` (`Core/Quant/`): OLS (linear/quadratic/cubic), **Theil–Sen** (median of pairwise slopes — robust to outlier POC bars), exponential (log-space OLS), logarithmic $a+b\ln(x+1)$, and **LOWESS** (locally-weighted linear, tricube kernel, half-sample span). The **virtual predictor** extrapolates each enabled fit $N$ bars out and draws their mean as the consensus. Per-project [README](../src/TradingTerminal.VolumeFootprint/README.md) has the exact forms.

### 4.2 Bookmap + VolBook — `TradingTerminal.Heatmap`

- **Liquidity heatmap** — resting L2 size per (price, time) cell on a magma ramp, **√-compressed** ($\sqrt{\text{size}}$) so large levels don't wash out small ones.
- **VWAP** — developing session $\dfrac{\sum p\,v}{\sum v}$.
- **CVD panel** — cumulative volume delta $\sum(\text{buy}-\text{sell})$ over per-column net-delta bars.
- **Session volume profile** — volume-at-price histogram; **POC** = max-volume bucket; **70% value area** (VAH/VAL) grown outward from the POC until 70% of session volume is enclosed.
- **Large-lot / iceberg** detection — a print is a large lot at $\ge 5\times$ the rolling-mean trade size; an iceberg is the same price+size **refilling $\ge 4\times$**.

### 4.3 Order book — `TradingTerminal.OrderBook`

Live L2 ladder; per-level bars normalised to the largest level on either side; **cumulative depth** per level $\sum_{j\le i} Q_j$. Read-outs: best bid/ask, spread $a-b$, mid $(a+b)/2$.

### 4.4 Charts (TradingView-style) — `TradingTerminal.Charts`

Candles render in Lightweight Charts (WebView2) but **every overlay number is computed in C#** by the Core `Indicators` (§1.1) — SMA(20), EMA(50), RSI(14), MACD(12/26/9) — so chart, backtest, and live values agree exactly.

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
