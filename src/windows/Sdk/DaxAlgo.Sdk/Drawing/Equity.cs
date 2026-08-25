namespace DaxAlgo.Sdk.Drawing;

/// <summary>How an equity curve is drawn.</summary>
/// <param name="ShowDrawdown">Whether to shade the distance below the running peak.</param>
/// <param name="ShowPeak">Whether to trace the high-water mark.</param>
/// <param name="Baseline">Starting equity. The curve is toned against it, so a strategy that is down
/// on the day reads as down at a glance rather than after reading the axis.</param>
/// <param name="ValueFormat">Numeric format for the axis and the readout.</param>
public readonly record struct EquityOptions(
    bool ShowDrawdown = true,
    bool ShowPeak = true,
    double Baseline = double.NaN,
    string? ValueFormat = null)
{
    /// <summary>The intended defaults.</summary>
    public static EquityOptions Default { get; } = new(ShowDrawdown: true, ShowPeak: true);
}

/// <summary>What an equity curve told us.</summary>
/// <param name="Range">The value range drawn, for plotting anything else on the same scale.</param>
/// <param name="Peak">Highest equity reached.</param>
/// <param name="MaxDrawdown">Largest peak-to-trough fall, as a positive number.</param>
/// <param name="MaxDrawdownShare">The same fall as a share of the peak it fell from, or NaN.</param>
public readonly record struct EquitySummary(
    PlotRange Range, double Peak, double MaxDrawdown, double MaxDrawdownShare);

/// <summary>
/// The strategy's own money: equity over time, with the drawdown shaded underneath.
///
/// <para>Every strategy has one of these and it is the picture a person actually judges it by. It goes
/// in the SDK rather than being written per strategy because the drawdown shading is the part that
/// gets skipped, and a rising equity curve without it hides the thing that decides whether a strategy
/// is survivable — a curve that ends up 20% having been down 60% on the way is a different strategy
/// from one that ends up 20% having been down 5%, and they draw identically without this.</para>
///
/// <para>Returns the drawdown it measured, so the number on the tile and the shape on the chart come
/// from the same pass and cannot disagree.</para>
/// </summary>
public static class Equity
{
    /// <summary>Draws the curve and returns what it measured.</summary>
    public static EquitySummary Draw(
        IRenderSurface surface,
        IReadOnlyList<double>? equity,
        EquityOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (equity is null || equity.Count == 0)
            return new EquitySummary(PlotRange.Empty, double.NaN, double.NaN, double.NaN);

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return new EquitySummary(PlotRange.Empty, double.NaN, double.NaN, double.NaN);

        var baseline = double.IsFinite(options.Baseline) ? options.Baseline : equity[0];

        var range = PlotRange.Empty.Include(baseline);
        for (var index = 0; index < equity.Count; index++) range = range.Include(equity[index]);
        range = range.Padded();
        if (!range.IsValid) return new EquitySummary(PlotRange.Empty, double.NaN, double.NaN, double.NaN);

        Plot.HorizontalGrid(surface, range, format: options.ValueFormat);
        surface.AxisX(0d, Math.Max(1, equity.Count - 1));

        // One pass for the peak track, the drawdown shading and the worst fall — three answers that must
        // agree, and will not if they are computed in three places.
        var peak = double.NegativeInfinity;
        var worst = 0d;
        var worstShare = double.NaN;
        var step = area.StepX(equity.Count);

        for (var index = 0; index < equity.Count; index++)
        {
            var value = equity[index];
            if (!double.IsFinite(value)) continue;

            if (value > peak) peak = value;

            var fall = peak - value;
            if (fall > worst)
            {
                worst = fall;
                worstShare = peak > 0d ? fall / peak : double.NaN;
            }

            if (!options.ShowDrawdown || fall <= 0d) continue;

            var top = area.ToY(peak, range);
            var bottom = area.ToY(value, range);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Bearish), Alpha: 0.13d));
            surface.Rect(area.X + (index * step), top, Math.Max(1d, step), Math.Max(1d, bottom - top));
        }

        if (options.ShowPeak && double.IsFinite(peak))
        {
            var running = double.NegativeInfinity;
            surface.SetStyle(new RenderStyle(
                surface.Theme(RenderThemeColor.TextSecondary), Thickness: 1d, Alpha: 0.5d, Dashed: true));
            using var track = surface.Series("Peak", RenderSeriesKind.Steps);
            for (var index = 0; index < equity.Count; index++)
            {
                running = Math.Max(running, equity[index]);
                surface.Push(area.ToX(index, equity.Count), area.ToY(running, range));
            }
        }

        Levels.Draw(surface, baseline, "start", range, RenderThemeColor.Border, area);

        var last = equity[^1];
        Series.Draw(
            surface, "Equity", equity,
            SeriesOptions.Default.In(last >= baseline ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
            range, area);

        Plot.Crosshair(surface, range, options.ValueFormat);
        return new EquitySummary(range, peak, worst, worstShare);
    }
}
