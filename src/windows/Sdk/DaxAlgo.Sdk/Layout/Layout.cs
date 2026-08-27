namespace DaxAlgo.Sdk.Layout;

/// <summary>
/// Builds a unit's window layout.
///
/// <para>This is the vocabulary Hyperion composes windows from, so it is written to be read back: a
/// layout should say what the window looks like without the reader tracing arithmetic. Compare the two
/// ways of getting an order book beside a chart —</para>
///
/// <code>
/// // With this:
/// public UnitLayout Layout => Layout.Columns(
///     Layout.Panel("Price", DrawChart).Star(3),
///     Layout.Panel("Book", DrawBook).Pixels(260));
///
/// // Without it, inside one Draw, and every widget's rectangle computed by hand:
/// var area = PlotArea.Of(surface);
/// var (book, chart) = area.SplitRight(260d);
/// DrawChart(surface, chart);
/// DrawBook(surface, book);
/// </code>
///
/// <para>The second still works and is right for subdividing <i>one</i> panel. The difference is that
/// panels built here are real: each gets its own surface and viewport, its own header, and a separator
/// the user can drag. <see cref="Drawing.PlotArea"/> divides a picture; this divides a window.</para>
/// </summary>
public static class Layout
{
    /// <summary>One drawable panel with a header.</summary>
    /// <param name="title">Header text. Empty for a full-bleed panel with no header.</param>
    /// <param name="draw">This panel's frame callback — pure and fast, like any other draw.</param>
    public static PanelNode Panel(string title, Action<IRenderSurface> draw) =>
        new(title ?? string.Empty, draw);

    /// <summary>One drawable panel with no header.</summary>
    public static PanelNode Panel(Action<IRenderSurface> draw) => Panel(string.Empty, draw);

    /// <summary>Children stacked top to bottom, with a draggable separator between neighbours.</summary>
    public static SplitNode Rows(params LayoutNode[] children) =>
        new(SplitOrientation.Rows, children ?? []);

    /// <summary>Children placed left to right, with a draggable separator between neighbours.</summary>
    public static SplitNode Columns(params LayoutNode[] children) =>
        new(SplitOrientation.Columns, children ?? []);

    /// <summary>Gives this node <paramref name="weight"/> shares of the space left after the
    /// fixed-size siblings have taken theirs.</summary>
    public static TNode Star<TNode>(this TNode node, double weight = 1d)
        where TNode : LayoutNode =>
        (TNode)(node with { Size = PanelSize.Star(weight) });

    /// <summary>Pins this node to an exact height (in a row split) or width (in a column split).
    /// What an order-book ladder or a status strip wants — a fixed extent while the chart beside it
    /// takes the rest.</summary>
    public static TNode Pixels<TNode>(this TNode node, double extent)
        where TNode : LayoutNode =>
        (TNode)(node with { Size = PanelSize.Pixels(extent) });
}
