namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// The series key: a swatch and a name per series, along the top of the panel.
///
/// <para>A chart with three unlabelled lines on it is a chart nobody can read, and the label is the one
/// piece of a picture the author always knows and the reader never can. It is the cheapest thing on the
/// panel and the most often left out.</para>
/// </summary>
public static class Legend
{
    /// <summary>Draws a legend row from the same <see cref="SeriesData"/> the chart was drawn from, so
    /// the swatch colours cannot disagree with the lines.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<SeriesData>? series,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (series is null || series.Count == 0) return;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var x = area.X + 6d;
        var y = area.Y + 12d;

        for (var index = 0; index < series.Count; index++)
        {
            var name = series[index].Name;
            if (string.IsNullOrEmpty(name)) continue;

            var options = series[index].Options;
            var color = options.Thickness <= 0d ? SeriesOptions.Default.Color : options.Color;

            surface.SetStyle(new RenderStyle(surface.Theme(color), Thickness: 2d));
            surface.Line(x, y - 3d, x + 12d, y - 3d);

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 10d));
            surface.Text(x + 16d, y, name);

            // Estimated advance rather than a measured one: the surface has no text metrics by design,
            // because that would tie a sandboxed contract to the host's font stack.
            x += 16d + (name.Length * 6.2d) + 12d;
            if (x > area.Right - 40d) return;
        }
    }

    /// <summary>Draws a legend from name/colour pairs, for a picture that did not come from
    /// <see cref="Series.Chart"/>.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<(string Name, RenderThemeColor Color)>? entries,
        PlotArea area = default)
    {
        if (entries is null || entries.Count == 0) return;

        var series = new SeriesData[entries.Count];
        for (var index = 0; index < entries.Count; index++)
            series[index] = SeriesData.Line(entries[index].Name, [], entries[index].Color);

        Draw(surface, series, area);
    }
}
