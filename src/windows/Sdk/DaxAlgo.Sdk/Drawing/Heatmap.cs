namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a matrix of values is shaded.</summary>
/// <param name="Diverging">Whether the scale runs bearish→neutral→bullish around zero. False uses a
/// single-hue sequential ramp. A signed quantity on a sequential ramp hides which side of zero it is
/// on, which is normally the only thing worth knowing.</param>
/// <param name="Color">The role a sequential ramp runs towards. Ignored when diverging.</param>
/// <param name="Extent">The magnitude that counts as full strength. Zero derives it from the data in
/// view — which is what makes one cell's colour comparable with its neighbours but not with another
/// frame's.</param>
/// <param name="ShowValues">Whether to print the number in each cell. Only legible on a coarse grid;
/// the routine drops the text automatically when cells get small.</param>
/// <param name="Gap">Pixels left between cells.</param>
public readonly record struct HeatmapOptions(
    bool Diverging = true,
    RenderThemeColor Color = RenderThemeColor.Accent,
    double Extent = 0d,
    bool ShowValues = false,
    double Gap = 1d)
{
    /// <summary>The intended defaults.</summary>
    public static HeatmapOptions Default { get; } = new(Diverging: true, Gap: 1d);

    /// <summary>A single-hue ramp, for a magnitude with no sign — volume, dwell time, trade count.</summary>
    public static HeatmapOptions Magnitude(RenderThemeColor color = RenderThemeColor.Accent) =>
        Default with { Diverging = false, Color = color };
}

/// <summary>
/// A grid of shaded cells — a correlation matrix, liquidity over time and price, returns by
/// hour-of-day, a regime transition table.
///
/// <para>The general shape behind several specific pictures, and worth having as one widget because the
/// hard parts are shared: choosing the scale from the data in view, keeping the colour meaningful
/// against either theme, and knowing when the cells have got too small for their labels.</para>
/// </summary>
public static class Heatmap
{
    /// <summary>
    /// Draws a <paramref name="columns"/> × <paramref name="rows"/> grid, reading each cell from
    /// <paramref name="value"/>.
    ///
    /// <para>A delegate rather than an array so a caller can shade a matrix it already holds in whatever
    /// shape it holds it, without copying the whole thing into a new one every frame — this runs on the
    /// render thread.</para>
    /// </summary>
    public static void Draw(
        IRenderSurface surface,
        int columns,
        int rows,
        Func<int, int, double> value,
        HeatmapOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(value);
        if (columns <= 0 || rows <= 0) return;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var extent = options.Extent;
        if (extent <= 0d)
        {
            for (var column = 0; column < columns; column++)
            for (var row = 0; row < rows; row++)
            {
                var sample = value(column, row);
                if (double.IsFinite(sample)) extent = Math.Max(extent, Math.Abs(sample));
            }
        }

        if (extent <= 0d) return;

        var cellWidth = area.Width / columns;
        var cellHeight = area.Height / rows;
        var labels = options.ShowValues && cellWidth >= 34d && cellHeight >= 15d;

        for (var column = 0; column < columns; column++)
        for (var row = 0; row < rows; row++)
        {
            var sample = value(column, row);
            if (!double.IsFinite(sample)) continue;

            var color = options.Diverging
                ? ColorScale.Diverging(surface, sample, extent)
                : ColorScale.Sequential(surface, Math.Abs(sample) / extent, options.Color);

            var x = area.X + (column * cellWidth);
            var y = area.Y + (row * cellHeight);

            surface.SetStyle(new RenderStyle(color));
            surface.Rect(
                x + options.Gap, y + options.Gap,
                Math.Max(1d, cellWidth - (options.Gap * 2d)),
                Math.Max(1d, cellHeight - (options.Gap * 2d)));

            if (!labels) continue;

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 9d));
            surface.Text(x + 4d, y + (cellHeight / 2d) + 3d, sample.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Draws from a rectangular array, for a caller that already has one.</summary>
    public static void Draw(
        IRenderSurface surface,
        double[,]? values,
        HeatmapOptions options = default,
        PlotArea area = default)
    {
        if (values is null) return;

        Draw(surface, values.GetLength(0), values.GetLength(1), (c, r) => values[c, r], options, area);
    }

    /// <summary>
    /// Axis labels down the left and along the bottom.
    ///
    /// <para>Separate from <see cref="Draw"/> because a heatmap is often overlaid or placed in a tile
    /// where the labels would not fit, and an unlabelled correlation matrix is at least honest about
    /// being a texture. A mislabelled one is not.</para>
    /// </summary>
    public static void Labels(
        IRenderSurface surface,
        IReadOnlyList<string>? columns,
        IReadOnlyList<string>? rows,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9d));

        if (rows is { Count: > 0 })
        {
            var height = area.Height / rows.Count;
            if (height >= 11d)
            {
                for (var index = 0; index < rows.Count; index++)
                    surface.Text(area.X + 2d, area.Y + (index * height) + (height / 2d) + 3d, rows[index]);
            }
        }

        if (columns is { Count: > 0 })
        {
            var width = area.Width / columns.Count;
            if (width >= 24d)
            {
                for (var index = 0; index < columns.Count; index++)
                    surface.Text(area.X + (index * width) + 2d, area.Bottom - 3d, columns[index]);
            }
        }
    }
}
