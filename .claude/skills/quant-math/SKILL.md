---
name: quant-math
description: Quantitative math reference for DaxAlgo Terminal's signal/regime work — Ornstein-Uhlenbeck mean-reversion (SDE, half-life, MLE/OLS calibration), linear algebra (correlation/covariance, PCA, Cholesky, EWMA), 3D geometry for Helix cube/surface viz, microstructure stats (VPIN/toxicity, cumulative delta, imbalance, Kyle's lambda), Markov transition matrices, and volatility estimators. Load when a strategy/regime/correlation agent touches the math, so the formula and its numerically-stable form are correct before coding.
---

# Quant math — formulas + stable implementations

Reference for the math the strategy, regime, and correlation agents need. Each block gives the
formula, the **numerically stable** form to actually code, and where it lands in the repo. Math
in `Core/MarketData/Indicators` + `Microstructure`; calibration in the engine-side strategy.

## Ornstein–Uhlenbeck (mean reversion) — `strat-ornsteinuhlenbeck`

Continuous SDE: `dXₜ = θ(μ − Xₜ)dt + σ dWₜ` (θ>0 reversion speed, μ long-run mean, σ vol).

- **Discrete AR(1) form** (sample step Δt): `Xₜ = a + b·Xₜ₋₁ + ε`, fit by OLS, then
  - `θ = −ln(b) / Δt`   `μ = a / (1 − b)`   `σ² = Var(ε)·(−2 ln b) / (Δt (1 − b²))`
- **Half-life** of a shock: `t½ = ln(2) / θ` — the headline tradeable number (entry only when
  half-life is short enough to exit within the horizon).
- **Entry signal**: z-score `z = (Xₜ − μ) / σ_eq`, `σ_eq = σ / √(2θ)` (stationary std). Enter
  when `|z|` exceeds a band; size ∝ −z (fade the deviation).
- **Stability**: reject the fit if `b ≥ 1` (non-stationary — no mean reversion) or `b ≤ 0`.

## Linear algebra — `correlation`, regime PCA, correlated sims

- **Pearson correlation** `ρ = cov(x,y)/(σₓσᵧ)`. Compute cov with a **single-pass Welford**
  accumulator, not `E[xy]−E[x]E[y]` (catastrophic cancellation on large prices).
- **Spearman** = Pearson on ranks — prefer it for fat-tailed returns / outlier robustness.
- **EWMA covariance** (RiskMetrics): `Σₜ = λΣₜ₋₁ + (1−λ) rₜ rₜᵀ`, λ≈0.94 daily. Use for a
  responsive correlation matrix instead of an equal-weight window.
- **PSD repair**: a sample correlation matrix can be non-positive-semidefinite (missing data,
  shrinkage). Clip negative eigenvalues to 0 and renormalize the diagonal before using it.
- **PCA for regime axes**: eigendecompose the correlation matrix; PC1 loadings ≈ "market
  factor". Sort eigenvalues desc; explained-variance ratio = λᵢ/Σλ.
- **Cholesky** `Σ = L Lᵀ` to generate correlated normals (`x = L z`) for Monte-Carlo / synth
  feeds. Falls over if Σ isn't PSD — do the repair above first.

## 3D geometry — Helix cube/surface viz — `strat-orderflowcube`, `strat-orderflowsurfacespike`, `strat-indexkscoresurface`

- **Axis normalization**: each raw axis (price-Δ, delta, time, toxicity…) has wildly different
  units. Map to a common `[0,1]` (or `[−1,1]`) cube via robust min/max (5th–95th pctile, not
  true min/max — one outlier shouldn't flatten the cube). Keep the scale factors in the VM so
  hover tooltips can invert back to real units.
- **Color = 4th dimension**: map the scalar (e.g. intensity) through a perceptually-uniform
  ramp; clamp to the same robust percentile range.
- **Surface meshing**: a value grid `z = f(x,y)` becomes a Helix `MeshGeometry3D` — vertices on
  the (x,y) lattice, two triangles per cell, shared vertices (don't duplicate or normals break).
  Recompute normals on update or lighting looks flat.
- **Performance**: rebuild the mesh off the UI thread, assign the finished `Geometry3D` on the
  dispatcher once. Never mutate vertex collections per-tick on the UI thread.

## Microstructure — `strat-orderflowtoxicity`, `strat-cumulativedelta`, `strat-imbalanceheatfront`

- **Trade sign** (no quote-at-trade): **tick rule** (uptick=+1, downtick=−1, carry on equal) or
  **Lee–Ready** (compare to prevailing mid; at-mid → tick rule). Document which one.
- **Cumulative delta** = Σ signed volume; the *slope* and divergence vs price is the signal,
  not the level (which drifts).
- **Order-book imbalance** `(Σbid − Σask)/(Σbid + Σask)` over N levels — bounded [−1,1].
- **VPIN (toxicity)**: bucket trades into equal-**volume** buckets (not equal time); per bucket
  `|Vbuy − Vsell| / V`; VPIN = rolling mean over n buckets. Volume buckets are the whole point —
  don't use time buckets.
- **Kyle's λ** (price impact): regress Δprice on signed order flow; slope = λ (illiquidity).

## Markov regimes — `markovregime`

- **Transition matrix** `Pᵢⱼ = count(i→j)/count(i→·)`; rows sum to 1. Laplace-smooth (+α) so an
  unseen transition isn't probability 0.
- **Stationary distribution** π solves `πP = π` (left eigenvector for eigenvalue 1, normalized).
- **Forward / Viterbi** for HMM regime inference — work in **log space** (sum of log-probs) to
  avoid underflow over long sequences.

## Volatility — `strat-volatilitytargeted`, vol-of-vol

- **Realized vol** = √(Σ rᵢ² · annualization). **EWMA vol** `σ²ₜ = λσ²ₜ₋₁ + (1−λ)r²ₜ`.
- **Range estimators** (tighter than close-to-close): **Garman–Klass** uses OHLC; **Parkinson**
  uses high-low. Use when you have bars, not just closes.
- **Vol targeting**: position scale = `target_vol / realized_vol`, clamped to a max leverage.
- **Vol-of-vol** = stdev of a rolling vol series — the regime axis for unstable markets.

## Numerical hygiene (applies everywhere)

- Single-pass **Welford** for mean/variance; never `Σx²−(Σx)²/n`.
- Guard every divisor (`σ`, denominators, `1−b`) — return a neutral signal, never NaN, on
  degenerate windows.
- Work in **log-price returns** `ln(pₜ/pₜ₋₁)`, not raw price differences, for anything statistical.
- Warm-up: emit no signal until the window/estimator has enough samples; say how many.
