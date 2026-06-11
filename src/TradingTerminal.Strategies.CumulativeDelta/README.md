# Cumulative Delta Scalper

`TradingTerminal.Strategies.CumulativeDelta` — live signal window for strategy id **`cumulative.delta.scalper`**.

> Sniper-mode order-flow delta scalper. **Trade-tape aggressor delta** (with Core footprint
> clusters) — bid-tick proxy when no tape exists — summed over a sliding bar window; trigger on
> cumΔ crossing an **adaptive ±σ threshold**, gated by up to **6 confirmations**. Spread/ATR gates
> are in basis points of price, so any instrument works out of the box.
> **Display only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | optional |

The trade tape (IB) is the **primary** delta source: per-bar delta is true aggressor-side volume
(buy − sell contracts) and each closed bar yields a Core `FootprintBar` (volume-at-price, POC,
3:1 stacked imbalances) rendered in the footprint panel. On brokers without a tape the engine
falls back to the original bid-tick uptick/downtick proxy — the active mode shows as a
**Real tape / Bid-tick proxy** badge in the header.

## How it works

1. **Delta accumulation.** Prints (or bid ticks in proxy mode) accumulate into a per-bar delta on
   the chart timeframe. A rolling window of $N$ bar deltas gives the windowed cumulative delta
   $\Sigma\Delta$.
2. **Adaptive trigger.** A candidate fires when $\Sigma\Delta$ crosses $\pm\theta$. By default
   $\theta = k\,\sigma(\Sigma\Delta)$ over the recent distribution (k = `ThresholdSigma`), making
   the trigger scale-free across tick counts and contract volume; a fixed manual threshold is
   available.
3. **Pre-signal gates** (evaluated every bar, shown live in the GATES board): session window
   (GMT, Asia/London/NY/Overlap), spread cap in **bp of price**, ATR(14) band in **bp**, cooldown,
   daily/per-session caps.
4. **Confirmations** (up to 6, shown live in the CONFIRMATIONS board):
   momentum alignment (last 3 bar deltas), HTF EMA(50) on 15m, EMA slope, ADX(14) ≥ threshold,
   spread calm vs rolling average, and — tape mode only — **footprint stacked-imbalance agreement**
   (the last completed bar's 3:1 stack contrast must side with the signal).
5. Armed signals publish through the notifier; unarmed/low-confidence crossovers are logged as
   idle lines for tuning.

## The window

- **PRICE / DELTA panes share one time axis** — per-bar delta bars (sign-coloured) with the
  windowed cumΔ line and the live ±θ trigger levels.
- **FOOTPRINT CLUSTER** — per-price buy/sell volume, POC highlight, imbalance outlines, per-bar
  Δ/Σ footer (tape mode).
- **GATES + CONFIRMATIONS boards** — every gate and confirmation as a live ✓/✗ row with its
  current reading, so "why isn't it firing" is always visible.
- Resizable right panel; live-tunable parameters in the top strip (window, θ mode/σ, min conf,
  ADX, bp gates, sessions, caps).

## Project layout

| File | Role |
|---|---|
| `CumulativeDeltaStrategy.cs` | `ITradingStrategy` metadata (id, display name, data-requirement tags) |
| `Indicators.cs` | EMA / ADX / ATR helpers |
| `CumulativeDeltaViewModel.cs` | Live VM — tape/proxy delta, footprints, gates, confirmations, session state |
| `CumulativeDeltaWindow.xaml(.cs)` | Window — price/delta panes, footprint canvas, gate boards |
| `DependencyInjection.cs` | `AddCumulativeDeltaStrategy()` |

## Wiring

- **Engine impl: none.** This is a **live-only** strategy — no `IBacktestStrategy`, so it does not
  appear in the Backtest tab or CLI. All logic lives in the VM + `Indicators.cs`.
- **Streams:** `IMarketDataHub.Quotes/Bars/Trades(InstrumentId)` via `IMarketDataIngest`
  (`SubscribeTrades` is a no-op handle on brokers without a tape). Footprints via
  `Core.MarketData.FootprintFeatures.BuildBar` — the same extractor the Apex scalper and the
  Volume Footprint tool use.
- **DI:** `services.AddCumulativeDeltaStrategy()` from `AddStrategyPlugins()`; opened via
  `IStrategyFactory`.
