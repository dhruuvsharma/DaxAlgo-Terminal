namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a signed histogram is drawn.</summary>
/// <param name="Positive">Colour for bars above the baseline.</param>
/// <param name="Negative">Colour for bars below it.</param>
/// <param name="Baseline">The value bars are measured from — zero for delta or MACD, and something
/// else only rarely.</param>
/// <param name="BarFraction">Bar width as a fraction of the column, leaving the rest as a gap.</param>
/// <param name="Alpha">0 transparent to 1 opaque.</param>
/// <param name="ShowBaseline">Whether to stroke the baseline itself.</param>
public readonly record struct HistogramOptions(
    RenderThemeColor Positive = RenderThemeColor.Bullish,
    RenderThemeColor Negative = RenderThemeColor.Bearish,
    double Baseline = 0d,
    double BarFraction = 0.72d,
    double Alpha = 0.9d,
    bool ShowBaseline = true)
{
    /// <summary>The intended defaults. Explicit arguments because <c>new()</c> on a record struct skips
    /// the primary constructor's defaults and would leave zero-width, fully transparent bars.</summary>
    public static HistogramOptions Default { get; } = new(BarFraction: 0.72d, Alpha: 0.9d);

    /// <summary>One colour for every bar — for a quantity with no sign to it, like volume.</summary>
    public static HistogramOptions Single(RenderThemeColor color) =>
        Default with { Positive = color, Negative = color, ShowBaseline = false };
}

/// <summary>
/// Signed bars measured from a baseline — MACD histogram, cumulative delta, volume, net position.
///
/// <para>Separate from a <c>Bars</c> series because the baseline is the whole point. A per-interval
/// quantity plotted as a pushed sequence has to infer where zero is, and a histogram whose zero sits at
/// the bottom of the panel says "always positive" about a series that crosses.</para>
/// </summary>
public static class Histogram
{
    /// <summary>Draws the histogram and returns the range used.</summary>
    public static PlotRange Draw(
        IRenderSurface surface,
        IReadOnlyList<double>? values,
        HistogramOptions options = default,
        PlotRange range = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (values is null || values.Count == 0) return PlotRange.Empty;

        // Whole, not field by field. RenderThemeColor.Text is zero, so a zeroed struct's "colour the
        // caller chose" is Text for both roles — and every bar comes out identical regardless of sign,
        // which is the one thing a signed histogram exists to show.
        if (options.BarFraction <= 0d) options = HistogramOptions.Default with { Baseline = options.Baseline };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        if (!range.IsValid)
        {
            // The baseline is always in range. A histogram scaled to its values alone can exclude its own
            // zero, and then every bar points the same way regardless of sign.
            range = PlotRange.Empty.Include(options.Baseline);
            for (var index = 0; index < values.Count; index++) range = range.Include(values[index]);
            range = range.Padded();
        }

        if (!range.IsValid) return PlotRange.Empty;

        var step = area.StepX(values.Count);
        var width = Math.Max(1d, step * options.BarFraction);
        var zeroY = area.ToY(options.Baseline, range);

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!double.IsFinite(value)) continue;

            var y = area.ToY(value, range);
            var top = Math.Min(y, zeroY);
            var height = Math.Abs(y - zeroY);
            if (height < 1d) height = 1d;

            var x = area.X + (index * step) + ((step - width) / 2d);

            surface.SetStyle(new RenderStyle(
                surface.Theme(value >= options.Baseline ? options.Positive : options.Negative),
                Alpha: options.Alpha));
            surface.Rect(x, top, width, height);
        }

        if (options.ShowBaseline)
        {
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.7d));
            surface.Line(area.X, zeroY, area.Right, zeroY);
        }

        return range;
    }

    /// <summary>Draws from a projection, so a caller need not flatten its own sample record first.</summary>
    public static PlotRange Draw<T>(
        IRenderSurface surface,
        IReadOnlyList<T>? items,
        Func<T, double> select,
        HistogramOptions options = default,
        PlotRange range = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(select);
        if (items is null || items.Count == 0) return PlotRange.Empty;

        var values = new double[items.Count];
        for (var index = 0; index < items.Count; index++) values[index] = select(items[index]);

        return Draw(surface, values, options, range, area);
    }
}
