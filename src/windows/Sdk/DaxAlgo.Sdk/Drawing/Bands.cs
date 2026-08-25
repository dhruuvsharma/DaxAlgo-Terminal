namespace DaxAlgo.Sdk.Drawing;

/// <summary>How an envelope is drawn.</summary>
/// <param name="Color">Theme role for the fill and the edges.</param>
/// <param name="FillAlpha">Opacity of the shaded interior. Low by design — a band is context behind
/// the price, and a fill that competes with it makes the price harder to read, not easier.</param>
/// <param name="EdgeAlpha">Opacity of the two edge strokes.</param>
/// <param name="ShowEdges">Whether to stroke the upper and lower edges at all.</param>
/// <param name="ShowMiddle">Whether to stroke the middle line when one is supplied.</param>
/// <param name="Steps">How many strips the fill is drawn as. The surface has no polygon primitive, so a
/// filled band is a run of rectangles; more strips is smoother and costs more of the frame budget.</param>
public readonly record struct BandOptions(
    RenderThemeColor Color = RenderThemeColor.Neutral,
    double FillAlpha = 0.14d,
    double EdgeAlpha = 0.55d,
    bool ShowEdges = true,
    bool ShowMiddle = true,
    int Steps = 0)
{
    /// <summary>The intended defaults. Explicit argument for the same reason every options record here
    /// has one: <c>new()</c> would make the band fully transparent and therefore invisible.</summary>
    public static BandOptions Default { get; } = new(FillAlpha: 0.14d, EdgeAlpha: 0.55d);
}

/// <summary>
/// A shaded envelope between two series — Bollinger, Keltner, Donchian, a VWAP band, a spread's fair
/// range.
///
/// <para>The picture almost every mean-reversion strategy owes its reader. Without it a chart shows a
/// price and a signal and leaves the threshold that produced the signal invisible, which makes the
/// strategy impossible to argue with.</para>
/// </summary>
public static class Bands
{
    /// <summary>Draws the envelope and returns the range used, so price can be plotted on the same scale.</summary>
    public static PlotRange Draw(
        IRenderSurface surface,
        IReadOnlyList<double>? upper,
        IReadOnlyList<double>? lower,
        IReadOnlyList<double>? middle = null,
        BandOptions options = default,
        PlotRange range = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (upper is null || lower is null) return PlotRange.Empty;

        var count = Math.Min(upper.Count, lower.Count);
        if (count == 0) return PlotRange.Empty;

        if (options.FillAlpha <= 0d && options.EdgeAlpha <= 0d) options = BandOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        if (!range.IsValid)
        {
            range = PlotRange.Empty;
            for (var index = 0; index < count; index++)
                range = range.Include(upper[index]).Include(lower[index]);
            range = range.Padded();
        }

        if (!range.IsValid) return PlotRange.Empty;

        var color = surface.Theme(options.Color);

        // The fill, as vertical strips. The surface has no polygon primitive — deliberately, since an
        // arbitrary polygon is the one drawing call a host cannot bound cheaply.
        if (options.FillAlpha > 0d)
        {
            var steps = options.Steps > 0 ? Math.Min(options.Steps, count) : Math.Min(count, 160);
            surface.SetStyle(new RenderStyle(color, Alpha: options.FillAlpha));

            for (var strip = 0; strip < steps; strip++)
            {
                var index = steps == 1 ? 0 : (int)((long)strip * (count - 1) / (steps - 1));
                var top = area.ToY(upper[index], range);
                var bottom = area.ToY(lower[index], range);
                if (!double.IsFinite(top) || !double.IsFinite(bottom)) continue;

                var x = area.X + (strip * (area.Width / steps));
                surface.Rect(x, Math.Min(top, bottom), area.Width / steps, Math.Abs(bottom - top));
            }
        }

        if (options.ShowEdges)
        {
            var edge = new SeriesOptions(
                RenderSeriesKind.Line, options.Color, Thickness: 1d, Alpha: options.EdgeAlpha);
            Series.Draw(surface, "Upper", upper, edge, range, area);
            Series.Draw(surface, "Lower", lower, edge, range, area);
        }

        if (options.ShowMiddle && middle is { Count: > 0 })
        {
            Series.Draw(
                surface, "Middle", middle,
                new SeriesOptions(RenderSeriesKind.Line, options.Color, Thickness: 1d, Alpha: 0.8d, Dashed: true),
                range, area);
        }

        return range;
    }
}
