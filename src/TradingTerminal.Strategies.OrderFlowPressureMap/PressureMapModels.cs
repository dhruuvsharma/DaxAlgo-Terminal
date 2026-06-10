using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>
/// One fully-evaluated cell: a single ticker during one completed 1-minute candle. Immutable —
/// produced by <see cref="PressureMapCalculator.Evaluate"/> at bar close and never mutated, so it
/// can cross the lock boundary into the render snapshot safely.
/// </summary>
public sealed record PressureCell(
    DateTime OpenTimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    double RelativeVolume,
    double CandlePosition,
    double PriceImpact,
    double BookImbalance,
    PressureSignal Signal,
    double Intensity);

/// <summary>
/// Immutable per-ticker row handed to the window for rendering. <see cref="Cells"/> is aligned to a
/// shared minute grid (oldest column 0 → newest column N-1); a null entry is a minute with no
/// completed candle for that ticker (drawn as the neutral "no data" colour).
/// </summary>
public sealed class PressureRowSnapshot
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<PressureCell?> Cells { get; init; }

    /// <summary>Latest mid/last price from the live quote stream.</summary>
    public double LastPrice { get; init; }

    /// <summary>Live book imbalance (top-5 depth if available, else top-of-book sizes).</summary>
    public double BookImbalance { get; init; }

    /// <summary>Relative volume of the most recent completed candle.</summary>
    public double RelativeVolume { get; init; }

    public PressureSignal LastSignal { get; init; }
    public DateTime? LastSignalTime { get; init; }

    /// <summary>True when the most recent completed candle carries a non-neutral signal — the primary
    /// sort key that floats "interesting right now" names to the top.</summary>
    public bool HasActiveSignal { get; init; }
}
