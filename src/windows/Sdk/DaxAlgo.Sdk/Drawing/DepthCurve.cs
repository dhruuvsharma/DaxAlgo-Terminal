using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a cumulative depth chart is drawn.</summary>
/// <param name="Levels">How many levels a side to walk. Zero means the whole book.</param>
/// <param name="FillAlpha">Opacity of the shaded area under each curve.</param>
/// <param name="ShowMid">Whether to mark the mid price.</param>
/// <param name="ShowSpread">Whether to shade the gap between best bid and best ask.</param>
public readonly record struct DepthCurveOptions(
    int Levels = 0,
    double FillAlpha = 0.22d,
    bool ShowMid = true,
    bool ShowSpread = true)
{
    /// <summary>The intended defaults.</summary>
    public static DepthCurveOptions Default { get; } = new(FillAlpha: 0.22d);
}

/// <summary>
/// The depth chart: cumulative resting size on each side, price across, size up.
///
/// <para>The other way to look at a book, and it answers a different question from
/// <see cref="Ladder"/>. A ladder shows what is at each price; this shows what it would cost to walk
/// through them — where the wall is, and how far price would travel to clear a given size. A strategy
/// sizing an order against available liquidity is reading this shape whether or not it draws it.</para>
/// </summary>
public static class DepthCurve
{
    /// <summary>Draws both sides and returns the price range used.</summary>
    public static PlotRange Draw(
        IRenderSurface surface,
        DepthSnapshot? depth,
        DepthCurveOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (depth is null) return PlotRange.Empty;

        var bids = depth.Bids;
        var asks = depth.Asks;
        if (bids.Count == 0 && asks.Count == 0) return PlotRange.Empty;

        if (options.FillAlpha <= 0d) options = DepthCurveOptions.Default with { Levels = options.Levels };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        var take = options.Levels > 0 ? options.Levels : int.MaxValue;
        var bidCount = Math.Min(bids.Count, take);
        var askCount = Math.Min(asks.Count, take);

        var priceRange = PlotRange.Empty;
        for (var index = 0; index < bidCount; index++) priceRange = priceRange.Include(bids[index].Price);
        for (var index = 0; index < askCount; index++) priceRange = priceRange.Include(asks[index].Price);
        priceRange = priceRange.Padded(0.02d);
        if (!priceRange.IsValid) return PlotRange.Empty;

        // One size scale across both sides. Scaling each side to its own peak is the mistake that makes
        // a lopsided book look balanced — which is the single thing this picture exists to reveal.
        var peak = Math.Max(Cumulative(bids, bidCount), Cumulative(asks, askCount));
        if (peak <= 0d) return priceRange;

        if (options.ShowSpread && bids.Count > 0 && asks.Count > 0)
        {
            var left = ToX(area, Math.Min(depth.BestBid, depth.BestAsk), priceRange);
            var right = ToX(area, Math.Max(depth.BestBid, depth.BestAsk), priceRange);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Alpha: 0.18d));
            surface.Rect(left, area.Y, Math.Max(1d, right - left), area.Height);
        }

        Side(surface, area, bids, bidCount, priceRange, peak, RenderThemeColor.Bullish, options);
        Side(surface, area, asks, askCount, priceRange, peak, RenderThemeColor.Bearish, options);

        if (options.ShowMid && bids.Count > 0 && asks.Count > 0)
        {
            var x = ToX(area, (depth.BestBid + depth.BestAsk) / 2d, priceRange);
            surface.SetStyle(new RenderStyle(
                surface.Theme(RenderThemeColor.TextSecondary), Thickness: 1d, Alpha: 0.7d, Dashed: true));
            surface.Line(x, area.Y, x, area.Bottom);
        }

        return priceRange;
    }

    private static void Side(
        IRenderSurface surface, PlotArea area, IReadOnlyList<DepthLevel> levels, int count,
        PlotRange priceRange, double peak, RenderThemeColor color, DepthCurveOptions options)
    {
        if (count == 0) return;

        // Filled as vertical strips: the surface has no polygon primitive, so an area under a curve is a
        // run of rectangles.
        var running = 0d;
        var themed = surface.Theme(color);

        for (var index = 0; index < count; index++)
        {
            running += levels[index].Size;

            var x = ToX(area, levels[index].Price, priceRange);
            var nextX = index + 1 < count ? ToX(area, levels[index + 1].Price, priceRange) : x;
            var height = running / peak * area.Height;

            surface.SetStyle(new RenderStyle(themed, Alpha: options.FillAlpha));
            var left = Math.Min(x, nextX);
            surface.Rect(left, area.Bottom - height, Math.Max(1d, Math.Abs(nextX - x)), height);
        }

        // The step outline over the fill, so the wall reads as an edge rather than a shade change.
        running = 0d;
        surface.SetStyle(new RenderStyle(themed, Thickness: 1.5d));
        using var series = surface.Series(color == RenderThemeColor.Bullish ? "Bids" : "Asks", RenderSeriesKind.Steps);
        for (var index = 0; index < count; index++)
        {
            running += levels[index].Size;
            surface.Push(ToX(area, levels[index].Price, priceRange), area.Bottom - (running / peak * area.Height));
        }
    }

    private static double Cumulative(IReadOnlyList<DepthLevel> levels, int count)
    {
        var total = 0d;
        for (var index = 0; index < count; index++) total += levels[index].Size;
        return total;
    }

    private static double ToX(PlotArea area, double price, PlotRange range) =>
        range.IsValid
            ? area.X + ((price - range.Minimum) / range.Span * area.Width)
            : area.CenterX;
}
