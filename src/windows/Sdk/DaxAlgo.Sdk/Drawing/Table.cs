namespace DaxAlgo.Sdk.Drawing;

/// <summary>How a column is laid out.</summary>
/// <param name="Header">Column heading.</param>
/// <param name="Width">Share of the available width, relative to the other columns.</param>
/// <param name="AlignRight">Whether cells are right-aligned. True for numbers: a column of prices that
/// does not line up on the decimal point cannot be compared down the column, which is the only reason
/// to put numbers in a column.</param>
public readonly record struct TableColumn(string Header, double Width = 1d, bool AlignRight = false)
{
    /// <summary>A right-aligned column, for numbers.</summary>
    public static TableColumn Number(string header, double width = 1d) => new(header, width, AlignRight: true);
}

/// <summary>How a table is drawn.</summary>
/// <param name="RowHeight">Pixels per row.</param>
/// <param name="ShowHeader">Whether to draw the heading row.</param>
/// <param name="Stripe">Whether alternate rows are shaded — what makes a wide row readable across.</param>
/// <param name="MaxRows">Hard cap on rows drawn. Zero fits as many as the area holds.</param>
public readonly record struct TableOptions(
    double RowHeight = 16d,
    bool ShowHeader = true,
    bool Stripe = true,
    int MaxRows = 0)
{
    /// <summary>The intended defaults.</summary>
    public static TableOptions Default { get; } = new(RowHeight: 16d);
}

/// <summary>
/// Rows and columns of text — open positions, working orders, the top movers, a signal history, the
/// last N fills.
///
/// <para>Not everything a strategy has to say is a shape. Some of it is a list, and a list drawn as a
/// chart is worse than a list. This is the plainest widget here and probably the most used, because it
/// is what a strategy reaches for when it needs to show <i>what</i> rather than <i>how much</i>.</para>
///
/// <para>Bounded by the area: rows beyond what fits are not drawn, rather than painted past the bottom
/// edge where they cost frame budget and show nobody anything.</para>
/// </summary>
public static class Table
{
    /// <summary>Draws the table and returns how many rows were actually shown.</summary>
    public static int Draw(
        IRenderSurface surface,
        IReadOnlyList<TableColumn>? columns,
        IReadOnlyList<IReadOnlyList<string>>? rows,
        TableOptions options = default,
        PlotArea area = default,
        Func<int, RenderThemeColor>? toneOf = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (columns is null || columns.Count == 0 || rows is null || rows.Count == 0) return 0;

        if (options.RowHeight <= 0d) options = TableOptions.Default with { MaxRows = options.MaxRows };

        if (!area.IsValid) area = PlotArea.Of(surface);
        if (!area.IsValid) return 0;

        var totalWidth = 0d;
        for (var index = 0; index < columns.Count; index++) totalWidth += Math.Max(0.01d, columns[index].Width);

        var body = area;
        if (options.ShowHeader)
        {
            var (header, rest) = area.SplitTop(options.RowHeight);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));

            var x = header.X;
            for (var index = 0; index < columns.Count; index++)
            {
                var width = columns[index].Width / totalWidth * header.Width;
                Cell(surface, columns[index].Header, x, header.Y + options.RowHeight - 4d, width, columns[index].AlignRight);
                x += width;
            }

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.6d));
            surface.Line(header.X, header.Bottom, header.Right, header.Bottom);
            body = rest;
        }

        var fits = (int)Math.Floor(body.Height / options.RowHeight);
        var count = Math.Min(rows.Count, Math.Max(0, fits));
        if (options.MaxRows > 0) count = Math.Min(count, options.MaxRows);

        for (var row = 0; row < count; row++)
        {
            var y = body.Y + (row * options.RowHeight);

            if (options.Stripe && row % 2 == 1)
            {
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Surface), Alpha: 0.35d));
                surface.Rect(body.X, y, body.Width, options.RowHeight);
            }

            var tone = toneOf?.Invoke(row) ?? RenderThemeColor.Text;
            surface.SetStyle(new RenderStyle(surface.Theme(tone), FontSize: 10d));

            var cells = rows[row];
            var x = body.X;
            for (var index = 0; index < columns.Count; index++)
            {
                var width = columns[index].Width / totalWidth * body.Width;
                if (cells is not null && index < cells.Count)
                    Cell(surface, cells[index], x, y + options.RowHeight - 4d, width, columns[index].AlignRight);
                x += width;
            }
        }

        return count;
    }

    private static void Cell(IRenderSurface surface, string? text, double x, double y, double width, bool alignRight)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Estimated advance, not a measured one: the surface has no text metrics, deliberately — adding
        // them would tie a sandboxed drawing contract to the host's font stack.
        var estimated = text.Length * 6.1d;
        surface.Text(alignRight ? x + Math.Max(2d, width - estimated - 4d) : x + 3d, y, text);
    }
}
