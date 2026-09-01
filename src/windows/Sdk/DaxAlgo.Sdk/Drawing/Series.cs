namespace DaxAlgo.Sdk.Drawing;

/// <summary>How one series is drawn.</summary>
/// <param name="Kind">Line, Area, Steps, Bars or Scatter.</param>
/// <param name="Color">Theme role. Never a literal — a literal that reads well on a dark background is
/// invisible on a light one.</param>
/// <param name="Thickness">Stroke width.</param>
/// <param name="Alpha">0 transparent to 1 opaque.</param>
/// <param name="Dashed">Whether the stroke is dashed — the usual way to mark a projected or lagging
/// series without spending a second colour on it.</param>
public readonly record struct SeriesOptions(
    RenderSeriesKind Kind = RenderSeriesKind.Line,
    RenderThemeColor Color = RenderThemeColor.Accent,
    double Thickness = 1.5d,
    double Alpha = 1d,
    bool Dashed = false)
{
    /// <summary>The intended defaults. Written with an explicit argument because <c>new()</c> on a record
    /// struct binds to the implicit parameterless constructor and lands every field on zero — which would
    /// make this a zero-thickness, fully transparent line.</summary>
    public static SeriesOptions Default { get; } = new(Thickness: 1.5d, Alpha: 1d);

    /// <summary>The same options in another colour — the usual way to draw a second series.</summary>
    public SeriesOptions In(RenderThemeColor color) => this with { Color = color };
}

/// <summary>
/// One value per index, plotted across the panel.
///
/// <para>The most-repeated block in every authored picture: set a style, open a series, loop, push. Six
/// lines that are the same six lines every time, and every one of them is a place to get the scale, the
/// index or the theme role wrong. This is that block, once.</para>
///
/// <para>Every overload takes an explicit <see cref="PlotRange"/> so several series can share one scale.
/// Passing <c>default</c> asks the routine to scale from the values it was given, which is right for a
/// lone series and wrong for a comparison — two series auto-scaled separately look like they agree when
/// they do not.</para>
/// </summary>
public static class Series
{
    /// <summary>Draws one series and returns the range used, so a caller can reuse it for the grid.</summary>
    public static PlotRange Draw(
        IRenderSurface surface,
        string name,
        IReadOnlyList<double>? values,
        SeriesOptions options = default,
        PlotRange range = default,
        PlotArea area = default,
        IReadOnlyList<double>? at = null,
        PlotRange axis = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (values is null || values.Count == 0) return PlotRange.Empty;

        // Positions are honoured only when there is one for every value. A short array would silently
        // plot part of the series against the clock and the rest against nothing, which is worse than
        // ignoring it — and the caller has a bug worth noticing rather than half-drawing.
        if (at is not null && at.Count != values.Count) at = null;
        if (at is not null && !axis.IsValid) axis = Plot.RangeOf(at, static v => v);

        if (options.Thickness <= 0d) options = SeriesOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        if (!range.IsValid) range = Plot.RangeOf(values, static v => v).Padded();
        if (!range.IsValid) return PlotRange.Empty;

        surface.SetStyle(new RenderStyle(
            surface.Theme(options.Color), options.Thickness, options.Alpha, options.Dashed));

        // Bars are rectangles rather than a pushed sequence: a Bars series pushed as points has to guess
        // its own baseline, and for a per-interval quantity that guess is nearly always wrong.
        if (options.Kind == RenderSeriesKind.Bars)
        {
            Histogram.Draw(surface, values, new HistogramOptions(
                Positive: options.Color, Negative: options.Color, Alpha: options.Alpha), range, area);
            return range;
        }

        using var series = surface.Series(name ?? string.Empty, options.Kind);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!double.IsFinite(value)) continue;

            // By clock when positions were given, by index otherwise. Index spacing stays the default
            // because it is right for a bar series, where each column IS an interval.
            var x = at is not null && axis.IsValid && double.IsFinite(at[index])
                ? area.ToX(at[index], axis)
                : area.ToX(index, values.Count);

            surface.Push(x, area.ToY(value, range));
        }

        return range;
    }

    /// <summary>Draws a series from a projection, so a caller need not materialise a
    /// <c>double[]</c> from its own sample record just to plot one field of it.</summary>
    /// <param name="at">
    /// Where each item sits on the X axis, or null for even spacing.
    ///
    /// <para>Here as well as on the array overload, because a capability on one of two paths is a
    /// capability half the callers cannot reach — and this is the overload a unit plotting from its
    /// own sample records uses, which is most of them. <paramref name="position"/> is usually the
    /// easier way in: it reads the timestamp off the same record.</para>
    /// </param>
    /// <param name="position">The X position of an item, read from the item itself — the projection
    /// equivalent of <paramref name="at"/>, and the one that cannot fall out of step with the values.</param>
    public static PlotRange Draw<T>(
        IRenderSurface surface,
        string name,
        IReadOnlyList<T>? items,
        Func<T, double> select,
        SeriesOptions options = default,
        PlotRange range = default,
        PlotArea area = default,
        IReadOnlyList<double>? at = null,
        PlotRange axis = default,
        Func<T, double>? position = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(select);
        if (items is null || items.Count == 0) return PlotRange.Empty;

        // Read off the items when a selector was given, so the positions cannot drift out of step with
        // the values the way a parallel array can.
        if (position is not null)
        {
            var read = new double[items.Count];
            for (var index = 0; index < items.Count; index++) read[index] = position(items[index]);
            at = read;
        }

        if (at is not null && at.Count != items.Count) at = null;
        if (at is not null && !axis.IsValid) axis = Plot.RangeOf(at, static v => v);

        if (options.Thickness <= 0d) options = SeriesOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        if (!range.IsValid) range = Plot.RangeOf(items, select).Padded();
        if (!range.IsValid) return PlotRange.Empty;

        if (options.Kind == RenderSeriesKind.Bars)
        {
            var projected = new double[items.Count];
            for (var index = 0; index < items.Count; index++) projected[index] = select(items[index]);
            return Draw(surface, name, projected, options, range, area, at, axis);
        }

        surface.SetStyle(new RenderStyle(
            surface.Theme(options.Color), options.Thickness, options.Alpha, options.Dashed));

        using var series = surface.Series(name ?? string.Empty, options.Kind);
        for (var index = 0; index < items.Count; index++)
        {
            var value = select(items[index]);
            if (!double.IsFinite(value)) continue;

            var x = at is not null && axis.IsValid && double.IsFinite(at[index])
                ? area.ToX(at[index], axis)
                : area.ToX(index, items.Count);

            surface.Push(x, area.ToY(value, range));
        }

        return range;
    }

    /// <summary>
    /// The whole chart in one call: grid, axes, one or more series, legend and crosshair, all on a shared
    /// scale.
    ///
    /// <para>What a picture of "these three indicators" should cost. The shared scale is the part worth
    /// having — series drawn together but scaled separately is the single most misleading chart a
    /// generated visualizer produces, because it looks exactly like a correct one.</para>
    /// </summary>
    public static PlotRange Chart(
        IRenderSurface surface,
        IReadOnlyList<SeriesData> series,
        string? valueFormat = null,
        bool legend = true,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (series is null || series.Count == 0) return PlotRange.Empty;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return PlotRange.Empty;

        var range = PlotRange.Empty;
        var count = 0;
        for (var index = 0; index < series.Count; index++)
        {
            var values = series[index].Values;
            if (values is null) continue;

            count = Math.Max(count, values.Count);
            for (var i = 0; i < values.Count; i++) range = range.Include(values[i]);
        }

        range = range.Padded();
        if (!range.IsValid) return PlotRange.Empty;

        // ONE x range across every series. Computed here rather than per series: two series covering
        // different spans would each fill the panel and cross at a point that means nothing.
        var axis = PlotRange.Empty;
        var positioned = false;
        for (var index = 0; index < series.Count; index++)
        {
            var at = series[index].At;
            if (at is null || series[index].Values is null || at.Count != series[index].Values.Count) continue;

            positioned = true;
            for (var i = 0; i < at.Count; i++) axis = axis.Include(at[i]);
        }

        // The area, threaded through. Chart already computed it for the series and the legend and then
        // drew its furniture without it, so the grid and the readout escaped the region the caller
        // asked for.
        Plot.HorizontalGrid(surface, range, format: valueFormat, area: area);

        // The declared axis is what the host maps a pointer back through, so it has to be the axis the
        // points were actually placed on — declaring an index range under time-placed points is how a
        // crosshair ends up reading the wrong value.
        if (positioned && axis.IsValid) surface.AxisX(axis.Minimum, axis.Maximum);
        else surface.AxisX(0d, Math.Max(1, count - 1));

        for (var index = 0; index < series.Count; index++)
        {
            Draw(surface, series[index].Name, series[index].Values, series[index].Options, range, area,
                positioned ? series[index].At : null, axis);
        }

        if (legend) Legend.Draw(surface, series, area);

        Plot.Crosshair(surface, range, valueFormat, area);
        return range;
    }
}

/// <summary>One named series and how to draw it — the unit <see cref="Series.Chart"/> composes.</summary>
/// <param name="Name">Legend label.</param>
/// <param name="Values">One value per index.</param>
/// <param name="Options">Kind, colour, stroke.</param>
/// <param name="At">
/// Where each value sits on the X axis, or null for even spacing.
///
/// <para>Anything monotonic: seconds since the session opened, a Unix timestamp, a bar index that
/// skips a weekend. With it, a gap in the data is a gap in the picture; without it every point is one
/// step from the last however long the market was shut.</para>
/// </param>
public readonly record struct SeriesData(
    string Name,
    IReadOnlyList<double> Values,
    SeriesOptions Options = default,
    IReadOnlyList<double>? At = null)
{
    /// <summary>A line in the accent colour — the common case.</summary>
    public static SeriesData Line(
        string name, IReadOnlyList<double> values, RenderThemeColor color = RenderThemeColor.Accent,
        IReadOnlyList<double>? at = null) =>
        new(name, values, SeriesOptions.Default.In(color), at);

    /// <summary>A step series, for something that holds until it changes: a position, a regime, a
    /// state. Interpolating between those is a lie about when the change happened.</summary>
    public static SeriesData Steps(
        string name, IReadOnlyList<double> values, RenderThemeColor color = RenderThemeColor.Neutral,
        IReadOnlyList<double>? at = null) =>
        new(name, values, SeriesOptions.Default with { Kind = RenderSeriesKind.Steps, Color = color }, at);

    /// <summary>A dashed line, for a projected or lagging series.</summary>
    public static SeriesData Dashed(
        string name, IReadOnlyList<double> values, RenderThemeColor color = RenderThemeColor.Neutral,
        IReadOnlyList<double>? at = null) =>
        new(name, values, SeriesOptions.Default with { Color = color, Dashed = true }, at);
}
