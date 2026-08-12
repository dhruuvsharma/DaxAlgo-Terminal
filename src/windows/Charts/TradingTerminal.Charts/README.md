# Charts

`TradingTerminal.Charts` — **Charts → Charts…**

> TradingView-style candlestick charting, rendered by **Lightweight Charts** inside a WebView2.
> All indicator numbers are computed in C# (`Core` indicators) — not in JS — so chart, backtest,
> and live-strategy values always agree. History loads from the repository, then the forming
> candle streams live from the hub.

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ (forming candle) | ✅ (history) | — | — |

## Settings

| Setting | Values | What it does |
|---|---|---|
| Instrument | searchable picker | broker universe + catalog |
| Timeframe | 1m / 5m / 15m / 1h (default) / 1D | bar size; history lookback is fixed per timeframe (below) |
| SMA | on/off, default **on** | simple moving average, period **20**, price pane |
| EMA | on/off, default **on** | exponential moving average, period **50**, price pane |
| RSI | on/off, default off | Relative Strength Index, period **14**, own pane with dashed 70/30 bands |
| MACD | on/off, default off | **12 / 26 / 9**, own pane: MACD line, signal line, histogram |

History lookback per timeframe: 1m → 2 days · 5m → 5 days · 15m → 15 days · 1h → 60 days ·
1D → 365 days.

## Colors

| Color | Element |
|---|---|
| `#26A69A` teal | up candles + wicks, volume bars on up bars, MACD histogram ≥ 0, RSI 30-band |
| `#EF5350` red | down candles + wicks, volume bars on down bars, MACD histogram < 0, RSI 70-band |
| `#42A5F5` blue | SMA(20) line; MACD line |
| `#E0A000` amber | EMA(50) line; MACD signal line; legend symbol; crosshair labels |
| `#AB47BC` purple | RSI(14) line |
| `#0A0A0A` / `#161616` | chart background / grid lines |

The volume histogram sits at the bottom 18% of the price pane. RSI and MACD each open their own
pane below the price pane when enabled (MACD takes pane 2 if RSI is also on).

## How it works

1. On instrument/timeframe change the VM pulls bars from `IMarketDataRepository`
   (`SnapshotReady` → JS `setData`), computing any enabled indicators over the full history.
2. A live quote subscription via `IMarketDataIngest`/`IMarketDataHub` updates the forming candle
   (`CandleUpdated` → JS `update`) — OHLC extends in place until the bucket rolls.
3. The legend (top left) tracks the hovered/last bar: O/H/L/C, change %, volume.

The WebView2 hosts a single local `Assets/index.html` (no network); C# ↔ JS messaging is
one-directional JSON pushes from the VM.

## Code map

| What | Where |
|---|---|
| VM (history + live + indicator computation) | `ChartsViewModel.cs` |
| WebView2 host + JS bridge | `ChartsWindow.xaml.cs` |
| Chart page + Lightweight Charts | `Assets/index.html`, `Assets/lightweight-charts.standalone.production.js` |
| Indicator implementations | `Core/MarketData/Indicators` (`SimpleMovingAverage`, `ExponentialMovingAverage`, `RelativeStrengthIndex`, MACD) |
