using System.Globalization;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>How the price gutter is drawn.</summary>
/// <param name="Width">Gutter width in pixels, taken off the right of the area it is given.</param>
/// <param name="ApproximateTicks">Roughly how many labelled prices to write.</param>
/// <param name="Format">Numeric format for the labels. Null gives four decimals at most.</param>
/// <param name="ShowGrid">Whether to rule a gridline across the plot at each labelled price.</param>
/// <param name="FontSize">Label size.</param>
public readonly record struct PriceScaleOptions(
    double Width = 62d,
    int ApproximateTicks = 6,
    string? Format = null,
    bool ShowGrid = true,
    double FontSize = 10d)
{
    /// <summary>The intended defaults. Written with explicit arguments because <c>new()</c> on a record
    /// struct binds the implicit parameterless constructor and lands every field on zero — a
    /// zero-width gutter with no ticks in it.</summary>
    public static PriceScaleOptions Default { get; } =
        new(Width: 62d, ApproximateTicks: 6, ShowGrid: true, FontSize: 10d);
}

/// <summary>
/// The price gutter down the right-hand side, and the tags that live in it.
///
/// <para>The furniture that makes a chart readable and that a generated picture almost never had. A
/// candle series with no gutter says the price went up; the same series with one says what it went up
/// to, which is the only version anybody can act on.</para>
///
/// <para><see cref="Tag"/> is the other half, and the reason this is a widget rather than a loop over
/// <c>Plot.HorizontalGrid</c>: the last price, the crosshair, an entry and a stop all say what they
/// are by putting a value <b>in the gutter, beside the line</b> — not by writing text over the candles
/// where it collides with the data it is annotating.</para>
/// </summary>
public static class PriceScale
{
    /// <summary>Height of a tag pill. Sized so two adjacent tags are still separable.</summary>
    public const double TagHeight = 15d;

    /// <summary>
    /// Draws the gutter — ticks on a 1/2/5 progression, a label each, the separator, and a gridline
    /// across the plot — and returns <b>the plot area to its left</b>, so a caller lays a chart out by
    /// assignment rather than by arithmetic.
    /// </summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="range">The price range the gutter measures.</param>
    /// <param name="options">Width, tick count, format.</param>
    /// <param name="area">The chart region <b>including</b> the gutter. Omitted, the whole panel.</param>
    public static PlotArea Draw(
        IRenderSurface surface,
        PlotRange range,
        PriceScaleOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (options.Width <= 0d || options.ApproximateTicks <= 0) options = PriceScaleOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotArea.None;

        var (gutter, plot) = area.SplitRight(Math.Min(options.Width, area.Width));
        if (!plot.IsValid || !range.IsValid) return plot;

        surface.AxisY(range.Minimum, range.Maximum, options.Format);

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.7d));
        surface.Line(gutter.X, area.Y, gutter.X, area.Bottom);

        var step = Plot.NiceStep(range.Span / options.ApproximateTicks);
        if (!double.IsFinite(step) || step <= 0d) return plot;

        var grid = surface.Theme(RenderThemeColor.Grid);
        var text = surface.Theme(RenderThemeColor.TextSecondary);

        for (var value = Math.Ceiling(range.Minimum / step) * step; value <= range.Maximum; value += step)
        {
            var y = plot.ToY(value, range);

            if (options.ShowGrid)
            {
                surface.SetStyle(new RenderStyle(grid, Thickness: 1d, Alpha: 0.5d));
                surface.Line(plot.X, y, plot.Right, y);
            }

            surface.SetStyle(new RenderStyle(grid, Thickness: 1d, Alpha: 0.7d));
            surface.Line(gutter.X, y, gutter.X + 3d, y);

            surface.SetStyle(new RenderStyle(text, FontSize: options.FontSize));
            surface.Text(gutter.X + 6d, y + (options.FontSize / 3d), Format(value, options.Format));
        }

        return plot;
    }

    /// <summary>
    /// A filled pill in the gutter at one price — the last trade, the crosshair, a level.
    ///
    /// <para>Skipped rather than clamped when the price is off the scale. A tag pinned to the top of
    /// the gutter reads as a real price at that height, and somebody will act on it.</para>
    /// </summary>
    /// <param name="surface">The surface to draw onto.</param>
    /// <param name="range">The range the gutter is measuring.</param>
    /// <param name="price">Where the tag sits.</param>
    /// <param name="text">What it says. Null formats the price.</param>
    /// <param name="color">Theme role for the fill — bullish or bearish for a last price, neutral for a
    /// crosshair, the level's own colour for a level.</param>
    /// <param name="options">Width and format, matching the gutter it is drawn in.</param>
    /// <param name="area">The chart region including the gutter, exactly as given to
    /// <see cref="Draw"/> — so the two cannot disagree about where the gutter is.</param>
    public static void Tag(
        IRenderSurface surface,
        PlotRange range,
        double price,
        string? text = null,
        RenderThemeColor color = RenderThemeColor.Accent,
        PriceScaleOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!range.IsValid || !double.IsFinite(price)) return;
        if (price < range.Minimum || price > range.Maximum) return;

        if (options.Width <= 0d || options.ApproximateTicks <= 0) options = PriceScaleOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var (gutter, plot) = area.SplitRight(Math.Min(options.Width, area.Width));
        if (!gutter.IsValid) return;

        var y = plot.IsValid ? plot.ToY(price, range) : area.ToY(price, range);
        var top = Math.Clamp(y - (TagHeight / 2d), area.Y, Math.Max(area.Y, area.Bottom - TagHeight));

        surface.SetStyle(new RenderStyle(surface.Theme(color), Alpha: 0.95d));
        surface.Rect(gutter.X + 1d, top, Math.Max(gutter.Width - 2d, 1d), TagHeight);

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Background), FontSize: options.FontSize));
        surface.Text(gutter.X + 5d, top + TagHeight - 4d, text ?? Format(price, options.Format));
    }

    private static string Format(double value, string? format) =>
        value.ToString(format ?? "0.####", CultureInfo.InvariantCulture);
}
