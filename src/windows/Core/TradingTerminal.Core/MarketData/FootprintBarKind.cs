namespace TradingTerminal.Core.MarketData;

/// <summary>
/// How prints are partitioned into footprint bars. Mirrors Bookmap's Footprint "Bar Type"
/// dropdown: time interval, reversal, range, and volume bars.
/// </summary>
public enum FootprintBarKind
{
    /// <summary>Fixed wall-clock span (e.g. 30s / 1m). Default and historical path.</summary>
    Time = 0,

    /// <summary>
    /// New bar when price reverses <c>N</c> ticks from the extreme of the forming bar
    /// (Bookmap Reversal Type).
    /// </summary>
    Reversal = 1,

    /// <summary>
    /// New bar when high−low of the forming bar exceeds <c>N</c> ticks (Bookmap Range Type).
    /// </summary>
    Range = 2,

    /// <summary>
    /// New bar when total traded volume in the forming bar reaches a threshold
    /// (Bookmap Volume Type).
    /// </summary>
    Volume = 3,
}
