namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// One scored sub-signal of the per-instrument composite. <see cref="Score"/> is normalised to
/// <c>[-1, +1]</c> (positive = bullish, negative = bearish). <see cref="Contribution"/> is the
/// points this signal adds to the signed composite (<c>Score × Weight × 100</c>). When
/// <see cref="Valid"/> is false the signal didn't have enough input data and is excluded from
/// the composite (its weight is redistributed across the remaining valid signals).
/// </summary>
public sealed record InstrumentSignalScore(
    InstrumentRegimeSignal Signal,
    double Score,
    double Weight,
    double Contribution,
    bool Valid,
    string Detail);
