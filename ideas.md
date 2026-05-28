# Strategy Ideas — Multi-Variable (3-Axis) Visualizations for Spot Instruments

Five strategy frameworks where three orthogonal signals form a regime "cube." Each octant of the cube maps to a tradeable setup. All work on spot instruments only — no options, no futures curve, no expiry.

**Build order:** start with #1 (Order Flow Cube) — strongest edge-per-complexity given current infra (tick capture working, multi-broker, ScottPlot 2D, no options yet). Work through the rest after.

**Visualization note:** for every cube below, build the **2D-with-color** version first (two axes as X/Y, third as point color or size, with a fading trail of the last N bars). Validate edge in backtest before adding true 3D rendering — 3D scatter plots consistently lead to worse decisions than 2D-plus-color equivalents due to occlusion and depth ambiguity. The actual edge is in the signal definitions, not the projection.

---

## 1. Order Flow Cube — START HERE

**Axes**
- X: **Cumulative Volume Delta** — running sum of (buy_vol − sell_vol) over N bars
- Y: **Aggressor Ratio** — % of volume that hit the ask vs lifted the bid
- Z: **Average Trade Size** — mean trade size, normalized by its 20-day average

**Data needed:** trades with aggressor flag. Already captured in the `trades` table.

**Regime read**
- High CVD + high aggressor ratio + large avg size → **institutional accumulation** → go long, hold
- Flat CVD + high aggressor ratio + small avg size → **retail noise** → fade
- Negative CVD + low aggressor ratio + large avg size → **distribution disguised as passive** → short on bounce

**Edge thesis:** institutions can't hide aggregate flow. Detecting *who* is trading (passive vs aggressive, large vs small) is information not visible in price-only indicators.

---

## 2. Microstructure Liquidity Cube

**Axes**
- X: **Spread** — bid-ask in ticks, z-scored
- Y: **Top-of-Book Imbalance** — bid_size / (bid_size + ask_size), so 0.5 = balanced
- Z: **Book Depth Slope** — how fast resting size grows away from the touch (flat = thin book, steep = deep)

**Data needed:** L2 depth. cTrader feed gives this; IB needs `reqMktDepth`; NT via level2.

**Regime read**
- Tight spread + bid-heavy imbalance + steep ask slope → **passive buyers stacked, sellers exhausted** → expect upward micro-drift
- Wide spread + balanced imbalance + flat depth → **liquidity vacuum** → don't trade, wait
- Bid-heavy imbalance that *suddenly* flips to ask-heavy → **spoof pull or sweep incoming** → exit longs

**Edge thesis:** the order book leaks intent before price moves. Bookmap-style scalping systematized.

---

## 3. Vol-of-Vol Regime Cube

**Axes**
- X: **Realized Volatility** — rolling 30-bar stdev of returns
- Y: **Vol-of-Vol** — stdev of the rolling-vol series ("is volatility steady or jumpy?")
- Z: **Return Skew** — third moment of returns over the same window (directional asymmetry)

**Data needed:** close prices only. Trivially backtestable from existing bar data.

**Regime read**
- Low vol + low vol-of-vol + ~0 skew → **calm grind regime** → mean-reversion strategies thrive
- Low vol + *rising* vol-of-vol + negative skew → **storm forming** → cut size, position for breakout
- High vol + high vol-of-vol + extreme skew → **panic regime** → momentum chase or stay flat

**Edge thesis:** vol-of-vol leads vol. Position **before** the regime change is obvious to chart-readers. This is the measurable form of what "dark volatility" gestures at.

---

## 4. Cross-Asset Risk Cube

**Axes**
- X: **SPY momentum** — 20-bar ROC z-score
- Y: **VIX-proxy momentum** — UVXY/VIXY as ETF proxy, or realized vol of SPY as fallback
- Z: **DXY momentum** — dollar index direction

**Data needed:** three spot ETFs via IB; FX feed for DXY.

**Regime read**
- SPY up + VIX down + DXY down → **risk-on rally** → long QQQ/SPY, long EM, short USD
- SPY down + VIX up + DXY up → **risk-off flight** → cash or short
- SPY up + VIX up → **divergence (rare)** → reduce conviction, regime transitioning

**Edge thesis:** risk-on/risk-off rotation is the most reliable macro structure in markets and is entirely spot-tradeable via ETFs.

---

## 5. Auction Market Theory Cube

**Axes**
- X: **Distance from session VWAP** — in stdevs
- Y: **Volume Node Strength** — volume at current price as % of session total (HVN = High Volume Node, LVN = Low Volume Node)
- Z: **Time of Day** — normalized 0–1 across the session

**Data needed:** tick data only. Already captured.

**Regime read**
- Far from VWAP + at LVN + late session → **rejection trade** → fade back to VWAP
- Near VWAP + at HVN + mid-session → **balance** → fade extremes of the value area
- Far from VWAP + at HVN + early session → **new value forming** → trend continuation

**Edge thesis:** Market Profile is the textbook framework for spot equity index futures and applies cleanly to spot ETFs.

---

## Implementation order (proposed)

1. **Order Flow Cube** — uses data we already capture, no new feeds needed
2. **Auction Market Theory Cube** — tick-data only, well-documented references
3. **Vol-of-Vol Regime Cube** — close prices only, easiest to backtest
4. **Cross-Asset Risk Cube** — needs multi-instrument plumbing across brokers
5. **Microstructure Liquidity Cube** — needs L2 depth wiring per broker (cTrader ready, IB/NT need work)

For each: define the three signals concretely as numbers, backtest a regime-switching strategy across octants, then build the 2D-with-color visualization to monitor it live.
