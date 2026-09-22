namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Accumulates <see cref="FootprintPrint"/>s into sealed <see cref="FootprintBar"/>s.
/// Implementations differ by seal rule (time / reversal / range / volume); all share the same
/// <see cref="FootprintFeatures.BuildBar"/> aggregation.
/// </summary>
public interface IFootprintBucketer
{
    /// <summary>Running cumulative delta through the last sealed bar.</summary>
    long CumulativeDelta { get; }

    /// <summary>
    /// Adds one print. Returns the sealed prior bar when this print opened a new bar and the
    /// prior bar held prints; otherwise null.
    /// </summary>
    FootprintBar? Add(FootprintPrint print);

    /// <summary>Rebuilds the forming (unsealed) bar, or null when nothing is open.</summary>
    FootprintBar? BuildForming();

    /// <summary>Clears state; optionally seeds cumulative delta.</summary>
    void Reset(long cumulativeDeltaSeed = 0);
}
