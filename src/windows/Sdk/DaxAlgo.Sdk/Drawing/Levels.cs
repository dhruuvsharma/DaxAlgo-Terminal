namespace DaxAlgo.Sdk.Drawing;

/// <summary>One horizontal reference line.</summary>
/// <param name="Value">Where it sits on the value axis.</param>
/// <param name="Label">Short text drawn at the left. Empty for an unlabelled line.</param>
/// <param name="Color">Theme role.</param>
/// <param name="Dashed">Dashed by default: a reference is context, and a solid line at the same weight
/// as the data competes with it.</param>
public readonly record struct Level(
    double Value,
    string? Label = null,
    RenderThemeColor Color = RenderThemeColor.Neutral,
    bool Dashed = true);

/// <summary>
/// Labelled horizontal reference lines — VWAP, the session high and low, a point of control, an entry
/// price, a stop, a take-profit.
///
/// <para>The cheapest way to make a chart argue for itself. A price series with a stop drawn on it says
/// what the strategy was risking; the same series without it says only what happened.</para>
/// </summary>
public static class Levels
{
    /// <summary>Draws each level that falls inside the range. Levels outside it are skipped rather than
    /// clamped to the edge, because a line pinned to the top of a panel reads as a real level at that
    /// price.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<Level>? levels,
        PlotRange range,
        PlotArea area = default,
        double alpha = 0.75d)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (levels is null || levels.Count == 0 || !range.IsValid) return;

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        for (var index = 0; index < levels.Count; index++)
        {
            var level = levels[index];
            if (!double.IsFinite(level.Value)) continue;
            if (level.Value < range.Minimum || level.Value > range.Maximum) continue;

            var y = area.ToY(level.Value, range);

            surface.SetStyle(new RenderStyle(
                surface.Theme(level.Color), Thickness: 1d, Alpha: alpha, Dashed: level.Dashed));
            surface.Line(area.X, y, area.Right, y);

            if (string.IsNullOrEmpty(level.Label)) continue;

            surface.SetStyle(new RenderStyle(surface.Theme(level.Color), FontSize: 9.5d, Alpha: alpha));
            surface.Text(area.X + 4d, y - 3d, level.Label!);
        }
    }

    /// <summary>One level, for the common case where a caller has exactly one to draw.</summary>
    public static void Draw(
        IRenderSurface surface,
        double value,
        string? label,
        PlotRange range,
        RenderThemeColor color = RenderThemeColor.Neutral,
        PlotArea area = default) =>
        Draw(surface, [new Level(value, label, color)], range, area);
}

/// <summary>How a shaded threshold zone is drawn.</summary>
/// <param name="Color">Theme role for the shading.</param>
/// <param name="Alpha">Opacity. Low: a zone sits behind the data, not over it.</param>
/// <param name="ShowEdges">Whether to stroke the zone's boundaries.</param>
public readonly record struct ZoneOptions(
    RenderThemeColor Color = RenderThemeColor.Warning,
    double Alpha = 0.12d,
    bool ShowEdges = true)
{
    /// <summary>The intended defaults.</summary>
    public static ZoneOptions Default { get; } = new(Alpha: 0.12d);
}

/// <summary>
/// A shaded horizontal band across the whole panel — an oscillator's overbought and oversold zones, a
/// tolerance around fair value, a regime threshold.
///
/// <para>What turns a bare oscillator into a readable one. An RSI drawn without its 30 and 70 asks the
/// reader to hold the thresholds in their head, and a generated visualizer that omits them has produced
/// a chart nobody can act on.</para>
/// </summary>
public static class Zones
{
    /// <summary>Shades between two values on the value axis.</summary>
    public static void Draw(
        IRenderSurface surface,
        double from,
        double to,
        PlotRange range,
        ZoneOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!range.IsValid || !double.IsFinite(from) || !double.IsFinite(to)) return;

        if (options.Alpha <= 0d) options = ZoneOptions.Default;
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var lower = Math.Max(Math.Min(from, to), range.Minimum);
        var upper = Math.Min(Math.Max(from, to), range.Maximum);
        if (upper <= lower) return;

        var top = area.ToY(upper, range);
        var bottom = area.ToY(lower, range);

        surface.SetStyle(new RenderStyle(surface.Theme(options.Color), Alpha: options.Alpha));
        surface.Rect(area.X, top, area.Width, bottom - top);

        if (!options.ShowEdges) return;

        surface.SetStyle(new RenderStyle(
            surface.Theme(options.Color), Thickness: 1d, Alpha: options.Alpha * 3d, Dashed: true));
        surface.Line(area.X, top, area.Right, top);
        surface.Line(area.X, bottom, area.Right, bottom);
    }
}
