namespace DaxAlgo.Sdk.Drawing;

/// <summary>One readout: a caption, the number, and optionally how it is doing.</summary>
/// <param name="Label">What it is. Short — a tile is read at a glance or not at all.</param>
/// <param name="Value">The number, already formatted. Formatting is the caller's because only the
/// caller knows the instrument's tick size and currency.</param>
/// <param name="Detail">Optional second line — a change, a denominator, a timestamp.</param>
/// <param name="Tone">Bullish, Bearish, Warning or Neutral. Colours the value only, never the caption,
/// so a screen of tiles does not turn into a traffic light.</param>
public readonly record struct Tile(
    string Label,
    string Value,
    string? Detail = null,
    RenderThemeColor Tone = RenderThemeColor.Text)
{
    /// <summary>A tile toned by the sign of a number — the usual case for PnL, delta or a spread.</summary>
    public static Tile Signed(string label, double amount, string value, string? detail = null) =>
        new(label, value, detail, amount switch
        {
            > 0d => RenderThemeColor.Bullish,
            < 0d => RenderThemeColor.Bearish,
            _ => RenderThemeColor.Text,
        });
}

/// <summary>How a tile strip is drawn.</summary>
/// <param name="Columns">Tiles per row. Zero lays them all in one row.</param>
/// <param name="Gap">Pixels between tiles.</param>
/// <param name="ShowPlate">Whether each tile sits on a filled plate.</param>
/// <param name="ValueSize">Font size for the number.</param>
public readonly record struct TileOptions(
    int Columns = 0,
    double Gap = 6d,
    bool ShowPlate = true,
    double ValueSize = 17d)
{
    /// <summary>The intended defaults.</summary>
    public static TileOptions Default { get; } = new(Gap: 6d, ValueSize: 17d);
}

/// <summary>
/// The numbers a strategy wants stated rather than plotted — position, PnL, exposure, win rate, the
/// current regime, time to the close.
///
/// <para>Every dashboard needs these and there is no chart shape for them: a single scalar plotted over
/// time answers a different question from the same scalar right now, and the second question is the one
/// a person watching a live strategy is usually asking.</para>
/// </summary>
public static class Tiles
{
    /// <summary>Lays the tiles out across the area and draws each one.</summary>
    public static void Draw(
        IRenderSurface surface,
        IReadOnlyList<Tile>? tiles,
        TileOptions options = default,
        PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (tiles is null || tiles.Count == 0) return;

        if (options.ValueSize <= 0d) options = TileOptions.Default with { Columns = options.Columns };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        var columns = options.Columns > 0 ? options.Columns : tiles.Count;
        var rows = (int)Math.Ceiling(tiles.Count / (double)columns);

        for (var index = 0; index < tiles.Count; index++)
        {
            var cell = area
                .Row(index / columns, rows, options.Gap)
                .Column(index % columns, columns, options.Gap);

            if (cell.IsValid) One(surface, tiles[index], options, cell);
        }
    }

    /// <summary>Draws a single tile into an exact rectangle, for a caller composing its own layout.</summary>
    public static void One(IRenderSurface surface, Tile tile, TileOptions options = default, PlotArea area = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (options.ValueSize <= 0d) options = TileOptions.Default with { ShowPlate = options.ShowPlate };
        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return;

        if (options.ShowPlate)
        {
            Plot.Plate(surface, area, 0.45d);
            Plot.Frame(surface, area, 0.35d);
        }

        var inner = area.Inset(8d, 6d);
        if (!inner.IsValid) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
        surface.Text(inner.X, inner.Y + 10d, tile.Label ?? string.Empty);

        surface.SetStyle(new RenderStyle(surface.Theme(tile.Tone), FontSize: options.ValueSize));
        surface.Text(inner.X, inner.Y + 10d + options.ValueSize + 4d, tile.Value ?? string.Empty);

        if (string.IsNullOrEmpty(tile.Detail)) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9d, Alpha: 0.85d));
        surface.Text(inner.X, inner.Y + 10d + options.ValueSize + 18d, tile.Detail!);
    }
}
