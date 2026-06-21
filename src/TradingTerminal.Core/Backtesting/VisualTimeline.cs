using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtesting;

/// <summary>One OHLC candle of the charted instrument, aggregated from quote mids over a fixed
/// interval — the backdrop the visual replay scrubs across.</summary>
public sealed record VisualBar(DateTime TimeUtc, double Open, double High, double Low, double Close);

/// <summary>A trade event to draw on the replay chart: an entry or exit of one round-trip, at the
/// price and time it happened, on a given side.</summary>
public sealed record TradeMarker(DateTime TimeUtc, double Price, OrderSide Side, bool IsEntry, InstrumentId Instrument);

/// <summary>
/// The optional per-run recording that powers the Studio's visual replay: an OHLC backdrop for the
/// charted instrument plus the trade markers to overlay. Captured only when
/// <see cref="RunSpec.Visual"/> is on, so normal runs and sweeps pay nothing for it. The equity /
/// drawdown sub-panels read <see cref="BacktestReport.Equity"/> directly, so they aren't duplicated
/// here. For portfolio runs this charts the primary instrument; per-instrument charts come later.
/// </summary>
public sealed record VisualTimeline(
    InstrumentId Instrument,
    IReadOnlyList<VisualBar> Bars,
    IReadOnlyList<TradeMarker> Markers);
