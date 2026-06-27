using TradingTerminal.Core.Domain;

namespace TradingTerminal.OrderBook;

/// <summary>One display row of the ladder. <see cref="BarFraction"/> is the level size relative to the
/// largest level on either side (0..1), used to draw the proportional depth bar in the view.</summary>
public sealed record OrderBookLevel(double Price, long Size, long Cumulative, double BarFraction);

/// <summary>A single executed trade captured for the heatmap overlay — price, size and (classified)
/// aggressor side. Kept minimal: it only carries what the render needs to plot a dot.</summary>
public sealed record TradeMark(double Price, long Size, AggressorSide Side);

/// <summary>
/// One time-slice of the book for the liquidity heatmap. The VM appends one of these on each capture
/// tick (a fixed cadence, independent of the depth update rate), building a left→right scrolling time
/// axis. The code-behind reads <see cref="OrderBookViewModel.HeatColumns"/> and paints a column per
/// entry: y = price, cell intensity = resting size, plus the best-bid/ask + microprice lines and any
/// trade marks. <see cref="Imbalance"/> feeds the bottom imbalance lane.
/// </summary>
public sealed class HeatColumn
{
    public required DateTime TimeUtc { get; init; }

    /// <summary>Resting bid levels at capture time (best first), price→size.</summary>
    public required IReadOnlyList<DepthLevel> Bids { get; init; }

    /// <summary>Resting ask levels at capture time (best first).</summary>
    public required IReadOnlyList<DepthLevel> Asks { get; init; }

    public required double BestBid { get; init; }
    public required double BestAsk { get; init; }

    /// <summary>Size-weighted microprice across the captured levels (leans toward the thinner side).</summary>
    public required double Microprice { get; init; }

    /// <summary>Cumulative top-N queue imbalance in [-1, 1]; positive ⇒ heavier bid book.</summary>
    public required double Imbalance { get; init; }

    /// <summary>Trades that printed during this column's window, or null when none / tape unavailable.</summary>
    public List<TradeMark>? Trades { get; set; }
}
