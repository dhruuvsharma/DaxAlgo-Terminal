using System.Globalization;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a depth ladder is drawn.</summary>
/// <param name="Levels">Price levels to show per side.</param>
/// <param name="RowHeight">Height of one price row, in pixels.</param>
/// <param name="PriceWidth">Width of the price gutter, in pixels.</param>
/// <param name="ShowSize">Whether to print the resting size on each row.</param>
/// <param name="PriceFormat">Numeric format for prices.</param>
/// <param name="FirstLevel">
/// How many levels in from the touch to start, per side — how far the book is scrolled.
///
/// <para><b>Here so that scrolling a deep book does not mean allocating in <c>Draw</c>.</b> A ladder
/// showing ten of forty levels could only be scrolled by handing this routine a sliced
/// <c>DepthSnapshot</c>, which means building two lists on the render thread every frame — the one
/// thing the drawing rules tell an author never to do. An index costs nothing and says the same.</para>
///
/// <para>Pair it with <c>Viewport.PanY</c> to make dragging scroll the book. Past the end of the book
/// the ladder simply runs out of rows, so a value nobody can reach is harmless.</para>
/// </param>
public readonly record struct LadderOptions(
    int Levels = 10,
    double RowHeight = 18d,
    double PriceWidth = 64d,
    bool ShowSize = true,
    string? PriceFormat = null,
    int FirstLevel = 0)
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
    /// <param name="area">Where to draw it. Omitted, the ladder fills the panel — which is right for a
    /// book that owns its own panel and wrong the moment it sits beside a chart. Until this parameter
    /// existed a ladder could not be PLACED at all: it read the viewport directly, so composing a book
    /// next to a price chart drew it across both.</param>
    public static void Draw(
        IRenderSurface surface,
        DepthSnapshot? depth,
        LadderOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (depth is null)
            return;

        if (options.Levels <= 0)
            options = LadderOptions.Default;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid)
            return;

        var levels = options.Levels;
        var rowHeight = options.RowHeight > 0d ? options.RowHeight : LadderOptions.Default.RowHeight;
        var priceWidth = Math.Min(options.PriceWidth, area.Width * 0.5d);
        var barWidth = area.Width - priceWidth;
        if (barWidth <= 0d)
            return;

        var first = Math.Max(0, options.FirstLevel);
        var asks = Take(depth.Asks, first, levels);
        var bids = Take(depth.Bids, first, levels);
        var peak = Peak(asks, bids);
        if (peak <= 0d)
            return;

        // Asks descend to the touch, bids continue downward, so the spread sits mid-panel and the
        // ladder reads the way a trader expects: sell side on top.
        var middle = area.Y + (area.Height / 2d);
        var bearish = surface.Theme(RenderThemeColor.Bearish);
        var bullish = surface.Theme(RenderThemeColor.Bullish);
        var label = surface.Theme(RenderThemeColor.TextSecondary);

        for (var index = 0; index < asks.Count; index++)
        {
            var top = middle - ((index + 1) * rowHeight);
            if (top + rowHeight < area.Y)
                break;

            Row(surface, asks[index], top, rowHeight, area.X, priceWidth, barWidth, peak, bearish, label, options);
        }

        for (var index = 0; index < bids.Count; index++)
        {
            var top = middle + (index * rowHeight);
            if (top > area.Bottom)
                break;

            Row(surface, bids[index], top, rowHeight, area.X, priceWidth, barWidth, peak, bullish, label, options);
        }

        // The touch itself, so the eye lands on the spread first.
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d));
        surface.Line(area.X, middle, area.Right, middle);
    }

    private static void Row(
        IRenderSurface surface,
        DepthLevel level,
        double top,
        double rowHeight,
        double left,
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
            surface.Rect(left + priceWidth, top + 1d, barWidth * fraction, Math.Max(rowHeight - 2d, 1d));
        }

        surface.SetStyle(new RenderStyle(label, FontSize: 10d));
        surface.Text(left + 2d, top + rowHeight - 5d, level.Price.ToString(options.PriceFormat ?? "0.####", CultureInfo.InvariantCulture));

        if (options.ShowSize && level.Size > 0L)
        {
            surface.Text(
                left + priceWidth + 4d,
                top + rowHeight - 5d,
                level.Size.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary><paramref name="levels"/> rows starting <paramref name="first"/> in from the touch.
    /// Allocates nothing when the whole side already fits and nothing is scrolled, which is the common
    /// case and the one that runs every frame.</summary>
    private static IReadOnlyList<DepthLevel> Take(
        IReadOnlyList<DepthLevel>? side, int first, int levels)
    {
        if (side is null || side.Count == 0)
            return [];
        if (first == 0 && side.Count <= levels)
            return side;
        if (first >= side.Count)
            return [];

        var count = Math.Min(levels, side.Count - first);
        var taken = new DepthLevel[count];
        for (var index = 0; index < count; index++)
            taken[index] = side[first + index];
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
