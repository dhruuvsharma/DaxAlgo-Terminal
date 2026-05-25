using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// Inputs to <see cref="InstrumentRegimeCalculator.Compute"/>. Bars are required;
/// <see cref="Depth"/> is optional and enables the three L2 signals when present. The bars
/// must be sorted oldest-first; the calculator validates length only.
/// </summary>
/// <param name="Symbol">Display label used in headers and notifications.</param>
/// <param name="Timeframe">Bar size of <paramref name="Bars"/>, surfaced in the UI.</param>
/// <param name="Bars">Oldest-first OHLCV bars. At least 30 are required for the bar-based
/// signals; fewer leaves Trend / Momentum / RSI marked as Valid=false.</param>
/// <param name="Depth">Latest L2 snapshot from the broker (cTrader today). Null on brokers
/// that don't expose depth — the calculator skips the three L2 signals and renormalises.</param>
public sealed record InstrumentRegimeInputs(
    string Symbol,
    BarSize Timeframe,
    IReadOnlyList<Bar> Bars,
    DepthSnapshot? Depth);
