using System.Globalization;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a depth ladder is drawn.</summary>
/// <param name="Levels">Price levels to show per side.</param>
/// <param name="RowHeight">Height of one price row, in pixels.</param>
/// <param name="PriceWidth">Width of the price gutter, in pixels.</param>
/// <param name="ShowSize">Whether to print the resting size on each row.</param>
/// <param name="PriceFormat">Numeric format for prices.</param>
public readonly record struct LadderOptions(
    int Levels = 10,
    double RowHeight = 18d,
    double PriceWidth = 64d,
    bool ShowSize = true,
    string? PriceFormat = null)
{
    /// <summary>
    /// The intended defaults.
    ///
    /// <para>Written with an explicit argument on purpose. <c>new()</c> on a record struct binds to the
    /// implicit parameterless constructor rather than the primary one, so every field lands on zero and
    /// the primary constructor's declared defaults are silently skipped — which made this the
    /// all-zero value, and made every routine that fell back to it draw nothing at all.</para>
    /// </summary>
    public static LadderOptions Default { get; } = new(Levels: 10);
}

/// <summary>
/// A depth ladder: price rows with a size bar per side, asks above bids, best prices meeting in the
/// middle.
///
/// <para>The bar length is proportional to the largest resting size <b>in view</b>, not to the whole
/// book — a ladder scaled to a far-touch iceberg shows nothing at the touch, which is where the
/// attention is.</para>
///
/// <para>Pure: it draws through <see cref="IRenderSurface"/> and holds no state, so a sandboxed
/// visualizer and a host panel produce the same picture from the same code.</para>
/// </summary>
public static class Ladder
{
    /// <summary>Draws the ladder into the current panel, in panel pixel space.</summary>
    public static void Draw(IRenderSurface surface, DepthSnapshot? depth, LadderOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (depth is null)
            return;

        if (options.Levels <= 0)
            options = LadderOptions.Default;

        var viewport = surface.Viewport;
        if (viewport.Width <= 0d || viewport.Height <= 0d)
            return;

        var levels = options.Levels;
        var rowHeight = options.RowHeight > 0d ? options.RowHeight : LadderOptions.Default.RowHeight;
        var priceWidth = Math.Min(options.PriceWidth, viewport.Width * 0.5d);
        var barWidth = viewport.Width - priceWidth;
        if (barWidth <= 0d)
            return;

        var asks = Take(depth.Asks, levels);
        var bids = Take(depth.Bids, levels);
        var peak = Peak(asks, bids);
        if (peak <= 0d)
            return;

        // Asks descend to the touch, bids continue downward, so the spread sits mid-panel and the
        // ladder reads the way a trader expects: sell side on top.
        var middle = viewport.Height / 2d;
        var bearish = surface.Theme(RenderThemeColor.Bearish);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var label = surface.Theme(RenderThemeColor.TextSecondary);

        for (var index = 0; index < asks.Count; index++)
        {
            var top = middle - ((index + 1) * rowHeight);
            if (top + rowHeight < 0d)
                break;

            Row(surface, asks[index], top, rowHeight, priceWidth, barWidth, peak, bearish, label, options);
        }

        for (var index = 0; index < bids.Count; index++)
        {
            var top = middle + (index * rowHeight);
            if (top > viewport.Height)
                break;

            Row(surface, bids[index], top, rowHeight, priceWidth, barWidth, peak, bullish, label, options);
        }

        // The touch itself, so the eye lands on the spread first.
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d));
        surface.Line(0d, middle, viewport.Width, middle);
    }

    private static void Row(
        IRenderSurface surface,
        DepthLevel level,
        double top,
        double rowHeight,
        double priceWidth,
        double barWidth,
        double peak,
        RenderColor fill,
        RenderColor label,
        LadderOptions options)
    {
        var fraction = Math.Clamp(level.Size / peak, 0d, 1d);
        if (fraction > 0d)
        {
            // Bars grow from the price gutter outward, so their left edges align and the eye can
            // compare lengths without re-anchoring on every row.
            surface.SetStyle(new RenderStyle(fill, Alpha: 0.32d));
            surface.Rect(priceWidth, top + 1d, barWidth * fraction, Math.Max(rowHeight - 2d, 1d));
        }

        surface.SetStyle(new RenderStyle(label, FontSize: 10d));
        surface.Text(2d, top + rowHeight - 5d, level.Price.ToString(options.PriceFormat ?? "0.####", CultureInfo.InvariantCulture));

        if (options.ShowSize && level.Size > 0L)
        {
            surface.Text(
                priceWidth + 4d,
                top + rowHeight - 5d,
                level.Size.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    private static IReadOnlyList<DepthLevel> Take(IReadOnlyList<DepthLevel>? side, int levels)
    {
        if (side is null || side.Count == 0)
            return [];
        if (side.Count <= levels)
            return side;

        var taken = new DepthLevel[levels];
        for (var index = 0; index < levels; index++)
            taken[index] = side[index];
        return taken;
    }

    /// <summary>The largest size in view — what the bars are scaled against.</summary>
    private static double Peak(IReadOnlyList<DepthLevel> asks, IReadOnlyList<DepthLevel> bids)
    {
        var peak = 0d;
        for (var index = 0; index < asks.Count; index++)
            peak = Math.Max(peak, asks[index].Size);
        for (var index = 0; index < bids.Count; index++)
            peak = Math.Max(peak, bids[index].Size);
        return peak;
    }
}
