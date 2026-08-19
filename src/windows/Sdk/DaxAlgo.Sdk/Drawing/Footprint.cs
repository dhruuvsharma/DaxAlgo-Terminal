using System.Globalization;
using TradingTerminal.Core.MarketData;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a volume footprint is drawn.</summary>
/// <param name="ColumnWidth">Width of one bar column, in pixels.</param>
/// <param name="RowHeight">Height of one price row, in pixels.</param>
/// <param name="PriceWidth">Width of the price gutter, in pixels.</param>
/// <param name="ShowCellVolumes">Whether to print buy/sell volume inside each cell.</param>
/// <param name="ShowPointOfControl">Whether to mark the point of control on each bar.</param>
/// <param name="ShowValueArea">Whether to shade the value area.</param>
/// <param name="ShowImbalances">Whether to outline imbalanced rows.</param>
/// <param name="PriceFormat">Numeric format for the price gutter.</param>
public readonly record struct FootprintOptions(
    double ColumnWidth = 74d,
    double RowHeight = 14d,
    double PriceWidth = 60d,
    bool ShowCellVolumes = true,
    bool ShowPointOfControl = true,
    bool ShowValueArea = true,
    bool ShowImbalances = true,
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
    public static FootprintOptions Default { get; } = new(ColumnWidth: 74d);
}

/// <summary>
/// A volume footprint: bars as columns, price as rows, and buy/sell volume split within each cell.
///
/// <para>Cell shading is scaled per bar rather than across the whole window. A single high-volume
/// bar would otherwise wash out every other column to near-black, and the reason to look at a
/// footprint is the distribution <em>within</em> each bar.</para>
///
/// <para>This is the most demanding picture in the benchmark set, and it is deliberately built from
/// nothing but the surface primitives — rects, lines and text — so it is proof that the contract is
/// expressive enough rather than an argument that it is.</para>
/// </summary>
public static class Footprint
{
    /// <summary>Draws the footprint into the current panel, in panel pixel space.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<FootprintBar>? bars,
        FootprintOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (bars is null || bars.Count == 0)
            return;

        if (options.ColumnWidth <= 0d || options.RowHeight <= 0d)
            options = FootprintOptions.Default;

        var viewport = surface.Viewport;
        if (viewport.Width <= 0d || viewport.Height <= 0d)
            return;

        var priceWidth = Math.Min(options.PriceWidth, viewport.Width * 0.4d);
        var range = PriceRange(bars);
        if (!range.IsValid)
            return;

        var rows = Math.Max((int)Math.Floor(viewport.Height / options.RowHeight), 1);
        var tick = range.Span / rows;
        if (!double.IsFinite(tick) || tick <= 0d)
            return;

        DrawPriceGutter(surface, range, rows, options, priceWidth);

        var buy = surface.Theme(RenderThemeColor.Bullish);
        var sell = surface.Theme(RenderThemeColor.Bearish);
        var text = surface.Theme(RenderThemeColor.Text);
        var accent = surface.Theme(RenderThemeColor.Accent);
        var border = surface.Theme(RenderThemeColor.Border);

        for (var index = 0; index < bars.Count; index++)
        {
            var left = priceWidth + (index * options.ColumnWidth);
            if (left > viewport.Width)
                break;

            var bar = bars[index];
            // Per-bar scaling: the point of a footprint is the shape inside each bar, and a single
            // heavy bar scaled globally would flatten every other column.
            var peak = PeakCell(bar);
            if (peak <= 0d)
                continue;

            if (options.ShowValueArea)
                DrawValueArea(surface, bar, range, viewport, left, options, accent);

            foreach (var row in bar.Rows)
            {
                var y = Plot.ToY(row.Price, range, viewport.Height) - (options.RowHeight / 2d);
                if (y + options.RowHeight < 0d || y > viewport.Height)
                    continue;

                DrawCell(surface, row, y, left, peak, options, buy, sell, text, border);
            }

            if (options.ShowPointOfControl)
            {
                surface.SetStyle(new RenderStyle(accent, Thickness: 1.5d));
                var pocY = Plot.ToY(bar.PocPrice, range, viewport.Height);
                surface.Line(left, pocY, left + options.ColumnWidth, pocY);
            }
        }
    }

    private static void DrawCell(
        IRenderSurface surface,
        FootprintFeatureRow row,
        double y,
        double left,
        double peak,
        FootprintOptions options,
        RenderColor buy,
        RenderColor sell,
        RenderColor text,
        RenderColor border)
    {
        var half = options.ColumnWidth / 2d;
        var height = Math.Max(options.RowHeight - 1d, 1d);

        // Sell on the left, buy on the right — the bid/ask convention a footprint reader expects.
        surface.SetStyle(new RenderStyle(sell, Alpha: Intensity(row.SellVolume, peak)));
        surface.Rect(left, y, half, height);

        surface.SetStyle(new RenderStyle(buy, Alpha: Intensity(row.BuyVolume, peak)));
        surface.Rect(left + half, y, half, height);

        if (options.ShowImbalances && (row.BidImbalance || row.AskImbalance))
        {
            // Outlined, not filled: an imbalance is a property OF the cell, so it must not compete
            // with the volume shading that the cell already encodes.
            surface.SetStyle(new RenderStyle(border, Thickness: 1d));
            surface.Rect(
                row.BidImbalance ? left : left + half,
                y,
                half,
                height,
                filled: false);
        }

        if (!options.ShowCellVolumes || options.RowHeight < 10d)
            return;

        surface.SetStyle(new RenderStyle(text, FontSize: 8.5d, Alpha: 0.9d));
        var baseline = y + height - 2d;
        if (row.SellVolume > 0L)
            surface.Text(left + 2d, baseline, row.SellVolume.ToString("N0", CultureInfo.InvariantCulture));
        if (row.BuyVolume > 0L)
            surface.Text(left + half + 2d, baseline, row.BuyVolume.ToString("N0", CultureInfo.InvariantCulture));
    }

    private static void DrawValueArea(
        IRenderSurface surface,
        FootprintBar bar,
        PlotRange range,
        RenderViewport viewport,
        double left,
        FootprintOptions options,
        RenderColor accent)
    {
        // The 70% value area, derived here rather than taken from the caller so a visualizer gets it
        // without having to reimplement the standard definition.
        var (low, high) = ValueArea(bar);
        if (!double.IsFinite(low) || !double.IsFinite(high) || high <= low)
            return;

        var top = Plot.ToY(high, range, viewport.Height);
        var bottom = Plot.ToY(low, range, viewport.Height);
        surface.SetStyle(new RenderStyle(accent, Alpha: 0.08d));
        surface.Rect(left, top, options.ColumnWidth, Math.Max(bottom - top, 1d));
    }

    /// <summary>The price band holding 70% of a bar's volume, expanded outward from the point of control.</summary>
    public static (double Low, double High) ValueArea(FootprintBar bar, double share = 0.7d)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.Rows.Count == 0)
            return (double.NaN, double.NaN);

        var ordered = bar.Rows.OrderByDescending(Total).ToArray();
        var target = ordered.Sum(Total) * Math.Clamp(share, 0d, 1d);
        if (target <= 0d)
            return (double.NaN, double.NaN);

        var accumulated = 0d;
        var low = double.PositiveInfinity;
        var high = double.NegativeInfinity;
        foreach (var row in ordered)
        {
            accumulated += Total(row);
            low = Math.Min(low, row.Price);
            high = Math.Max(high, row.Price);
            if (accumulated >= target)
                break;
        }

        return (low, high);
    }

    private static void DrawPriceGutter(
        IRenderSurface surface,
        PlotRange range,
        int rows,
        FootprintOptions options,
        double priceWidth)
    {
        var label = surface.Theme(RenderThemeColor.TextSecondary);
        surface.SetStyle(new RenderStyle(label, FontSize: 9d));

        var step = Plot.NiceStep(range.Span / Math.Max(rows / 4, 1));
        if (step <= 0d)
            return;

        var viewport = surface.Viewport;
        for (var price = Math.Ceiling(range.Minimum / step) * step; price <= range.Maximum; price += step)
        {
            var y = Plot.ToY(price, range, viewport.Height);
            surface.Text(2d, y + 3d, price.ToString(options.PriceFormat ?? "0.####", CultureInfo.InvariantCulture));
        }

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.6d));
        surface.Line(priceWidth, 0d, priceWidth, viewport.Height);
    }

    private static PlotRange PriceRange(IReadOnlyList<FootprintBar> bars)
    {
        var range = PlotRange.Empty;
        for (var index = 0; index < bars.Count; index++)
        {
            foreach (var row in bars[index].Rows)
                range = range.Include(row.Price);
        }

        return range.Padded(0.02d);
    }

    private static double PeakCell(FootprintBar bar)
    {
        var peak = 0d;
        foreach (var row in bar.Rows)
            peak = Math.Max(peak, Math.Max(row.BuyVolume, row.SellVolume));
        return peak;
    }

    /// <summary>Shading floor of 0.08 so a traded-but-tiny cell stays visible against an empty one.</summary>
    private static double Intensity(long volume, double peak) =>
        volume <= 0L ? 0.04d : Math.Clamp(volume / peak, 0.08d, 1d);

    private static double Total(FootprintFeatureRow row) => row.BuyVolume + row.SellVolume;
}
