# Filtered Order-Flow Imbalance

A **research-paper** strategy. Source: Aditya Nittur Anantha, Shashi Jain & Prithwish Maiti,
*"Order-Flow Filtration and Directional Association with Short-Horizon Returns"*,
arXiv:[2507.22712](https://arxiv.org/abs/2507.22712) (Dec 2025).

## What the paper shows

Order-book imbalance (OBI) is a strong short-horizon directional signal, but dense order flow is
polluted by *fleeting* orders (very short-lived, heavily-modified, spoof-like). The paper tests three
order-level structural filters — **lifetime** (`Tⱼ ≥ T̄`, 100 ms), **modification count**
(`Mⱼ ≤ M̄`, 3), **modification time** (`ΔMⱼ ≥ ΔM̄`, 50 ms) — and a three-step diagnostic ladder
(Pearson correlation → discretised regime association → Hawkes cross-excitation) on BankNifty futures.

**Key finding:** filtering the *standing book* OBI barely moves the needle, but applying the same
filters to the **parent orders of executed trades** and rebuilding the **trade-based** imbalance
`OBI(T) = (N_buy − N_sell)/(N_buy + N_sell)` (their Eq. 17) *systematically* strengthens the directional,
causal association with future returns. Strong OBI regimes excite same-sign return regimes.

## What this strategy implements

The implementable, robust core of the paper:

- **Trade-based `OBI(T)`** over a rolling event-time window (paper default `h = 10 s`), computed from
  the signed trade tape (tick-rule signing via `Microstructure.ClassifyAggressor`).
- **9-bin regime classification** (`Core/MarketData/OrderFlowImbalance.cs`), symmetric over [−1, 1]
  with a central neutral band, indices −4..+4.
- **Directional signal** on strong same-sign regimes (`|regime| ≥ StrongRegime`), held for
  `HoldSeconds` of event time or until the regime decays through neutral.
- **Filtered vs. unfiltered `OBI(T)` shown side-by-side** so the paper's central question — *does
  filtering fleeting flow sharpen the signal?* — is observable live.

### Data-fidelity note

The paper's filters operate on per-order lifecycle data (order id + full modification/cancel history)
available in NSE tick-by-tick feeds. The terminal's broker feeds expose a **signed trade tape** but
not order-by-order lifecycles, so the lifetime / modification filters are approximated at the tape
level by a **genuine-intent (min-trade-size) filter** that drops sub-threshold odd-lot / fleeting
prints — the implementable analog of removing trades whose parent orders wouldn't have survived
filtration. The filtered and unfiltered series are both tracked so the filter's effect is visible.

## Parameters

| Param | Meaning | Default |
|---|---|---|
| Window (s) | Backward window over which `OBI(T)` accumulates (`h`) | 10 |
| Min trade size | Kept-trade threshold for the filtered series | 2 |
| Strong regime | Regime-index magnitude (1–4) that arms a signal | 3 |
| Hold (s) | Event-time holding period before flatten | 5 |
| Quantity | Signal size (display only) | 1 |

## Data requirement

`L1 | Bars | TradeTape`. Needs a signed trade tape — IB and the Simulated backend provide one;
NinjaTrader / cTrader / Alpaca do not (the feed badge reflects this). **Display/signals only — no
live order execution.**

## Where it lives

- Engine signal logic: `Infrastructure/Backtest/Strategies/FilteredOrderFlowStrategy.cs`
  (also registered in the Backtest window catalog and the `daxalgo-backtest` CLI as `filteredOrderFlow`).
- OBI math: `Core/MarketData/OrderFlowImbalance.cs`.
- This project: live window + view-model wrapping the engine via `LiveSignalStrategyViewModelBase`.
