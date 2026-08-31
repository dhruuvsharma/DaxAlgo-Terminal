namespace DaxAlgo.Sdk.Layout;

/// <summary>How a child is sized inside its parent split.</summary>
public enum PanelSizeUnit
{
    /// <summary>A share of whatever space is left, after the fixed children have taken theirs.</summary>
    Star,

    /// <summary>An exact height or width in device-independent pixels.</summary>
    Pixels,
}

/// <summary>
/// A child's size within its parent.
///
/// <para><b>There is deliberately no "Auto".</b> A drawn panel has no intrinsic size — it paints
/// whatever rectangle it is given — so Auto would measure to zero and the panel would vanish. Star and
/// Pixels are the only two that mean anything here, and refusing to offer a third that silently
/// collapses is better than documenting a trap.</para>
/// </summary>
/// <param name="Value">Star weight, or a pixel count.</param>
/// <param name="Unit">Which of the two this is.</param>
public readonly record struct PanelSize(double Value, PanelSizeUnit Unit)
{
    /// <summary>One share of the remaining space — the default for every child.</summary>
    public static PanelSize Fill { get; } = new(1d, PanelSizeUnit.Star);

    /// <summary><paramref name="weight"/> shares of the remaining space.</summary>
    public static PanelSize Star(double weight) =>
        new(weight > 0d ? weight : 1d, PanelSizeUnit.Star);

    /// <summary>An exact extent. Values below one pixel are treated as one share instead, because a
    /// zero-height panel is never what an author meant.</summary>
    public static PanelSize Pixels(double extent) =>
        extent >= 1d ? new(extent, PanelSizeUnit.Pixels) : Fill;
}

/// <summary>Which way a split lays its children out.</summary>
public enum SplitOrientation
{
    /// <summary>Children stacked top to bottom.</summary>
    Rows,

    /// <summary>Children placed left to right.</summary>
    Columns,
}

/// <summary>
/// One node in a unit's window layout: either a panel to draw, or a split holding more nodes.
///
/// <para>This is <b>data and delegates, nothing else</b>. No WPF type appears anywhere in this
/// vocabulary, which is what lets an authored unit describe a multi-panel window without the host ever
/// mounting a control the author built. <c>IWpfVisualizer</c> tried the other way round — handing the
/// host a <c>FrameworkElement</c> — and was retired for exactly that reason: arbitrary WPF from an
/// untrusted author runs inside the application, and the isolation the sandbox exists for is gone the
/// moment the host mounts it.</para>
/// </summary>
public abstract record LayoutNode
{
    private protected LayoutNode()
    {
    }

    /// <summary>How this node is sized inside its parent. Ignored at the root.</summary>
    public PanelSize Size { get; init; } = PanelSize.Fill;

    /// <summary>How many panels this node contains, counting through every split beneath it.</summary>
    public abstract int PanelCount { get; }

    /// <summary>How deeply splits nest beneath this node. A lone panel is depth one.</summary>
    public abstract int Depth { get; }
}

/// <summary>
/// One drawable panel. The host gives it its own surface, so it has its own viewport, its own cursor,
/// and its own place in the window — which is the difference between this and subdividing a single
/// surface with <see cref="Drawing.PlotArea"/>.
/// </summary>
/// <param name="Title">
/// The panel's header. Empty means no header — right for a single full-bleed chart, wrong for one of
/// four panels a user has to tell apart.
/// </param>
/// <param name="Draw">
/// The frame callback for this panel, with the same contract as <c>IVisualizer.Draw</c>: pure, fast,
/// possibly called more than once per frame, and running on the render thread.
/// </param>
public sealed record PanelNode(string Title, Action<IRenderSurface> Draw) : LayoutNode
{
    /// <inheritdoc/>
    public override int PanelCount => 1;

    /// <inheritdoc/>
    public override int Depth => 1;
}

/// <summary>A row or column of child nodes, with a draggable separator between neighbours.</summary>
/// <param name="Orientation">Rows or columns.</param>
/// <param name="Children">In visual order: top to bottom, or left to right.</param>
public sealed record SplitNode(
    SplitOrientation Orientation,
    IReadOnlyList<LayoutNode> Children) : LayoutNode
{
    /// <inheritdoc/>
    public override int PanelCount
    {
        get
        {
            var total = 0;
            foreach (var child in Children) total += child.PanelCount;
            return total;
        }
    }

    /// <inheritdoc/>
    public override int Depth
    {
        get
        {
            var deepest = 0;
            foreach (var child in Children) deepest = Math.Max(deepest, child.Depth);
            return deepest + 1;
        }
    }
}

/// <summary>
/// A unit's window layout — the middle of its window, between the parameter expander and the activity
/// log.
///
/// <para>The host owns the chrome around this and the author owns what is inside it. That division is
/// why every authored window looks like the others without Hyperion generating a line of it, and why a
/// unit cannot forget the activity log or style the parameter expander wrongly.</para>
///
/// <para><b>Bounded on purpose.</b> An authored unit is untrusted input, and a layout is a tree it
/// supplies; <see cref="MaximumPanels"/> and <see cref="MaximumDepth"/> stop a pathological or
/// generated one from building a visual tree deep enough to take the window down. Over either limit
/// the layout is refused whole rather than truncated — half a dashboard is a worse answer than a clear
/// one.</para>
/// </summary>
public sealed record UnitLayout
{
    /// <summary>The most panels one window may hold. Well past any real trading layout; low enough
    /// that a generated tree cannot exhaust the renderer.</summary>
    public const int MaximumPanels = 16;

    /// <summary>
    /// How deeply the tree may nest, counting the panel itself: a lone panel is 1, a panel inside a
    /// split is 2, and so on. Six allows five levels of splitting, which is well past the layouts in
    /// issue #42 — "two books with a strip between them" is 2.
    /// </summary>
    public const int MaximumDepth = 6;

    private UnitLayout(LayoutNode? root) => Root = root;

    /// <summary>The layout tree, or null for the default single panel.</summary>
    public LayoutNode? Root { get; }

    /// <summary>
    /// One panel filling the body, drawn by the unit's own <c>Draw</c>. What a unit gets when it
    /// describes no layout at all — which is most of them, and remains a perfectly good window.
    /// </summary>
    public static UnitLayout Single { get; } = new((LayoutNode?)null);

    /// <summary>True when this is the plain single-panel default.</summary>
    public bool IsSingle => Root is null;

    /// <summary>
    /// Wraps a tree, or falls back to <see cref="Single"/> when it is missing, empty, or past the
    /// bounds above. <b>Never throws</b>: a bad layout costs the author their panel arrangement, not
    /// their window.
    /// </summary>
    public static UnitLayout Of(LayoutNode? root)
    {
        if (root is null) return Single;
        if (root.PanelCount is 0 or > MaximumPanels) return Single;
        if (root.Depth > MaximumDepth) return Single;
        if (!IsWellFormed(root)) return Single;

        return new UnitLayout(root);
    }

    // ── Building one ────────────────────────────────────────────────────────────────────────────
    //
    // The same three verbs as `Layout`, mirrored here for one reason: a unit declares its arrangement
    // in a property called `Layout`, and inside that class the identifier `Layout` binds to the
    // PROPERTY, not to the static class. So the natural spelling —
    //
    //     public UnitLayout Layout => Layout.Rows(Layout.Panel("Price", DrawChart));
    //
    // does not compile, and the working form is `DaxAlgo.Sdk.Layout.Layout.Rows(...)`, which nobody
    // writes on purpose. Both shipped exemplars carried that mouthful, and the layout skill taught the
    // spelling that fails. `UnitLayout` is not a member name, so it never shadows.
    //
    // These also wrap the result, which removes the second error in the same line: `Layout.Rows(...)`
    // returns a `SplitNode` and the property is a `UnitLayout`, with no conversion between them.

    /// <summary>
    /// Turns a tree into a layout wherever one is expected.
    ///
    /// <para>This is what lets <see cref="Rows"/> and <see cref="Columns"/> return a NODE — nestable,
    /// and <c>.Star()</c>/<c>.Pixels()</c>-able like any other child — while still satisfying a
    /// property typed <see cref="UnitLayout"/>. Returning a wrapped layout instead would make the
    /// outermost call the only one that compiles, which is the trap this whole block exists to
    /// remove.</para>
    /// </summary>
    public static implicit operator UnitLayout(LayoutNode? root) => Of(root);

    /// <summary>Panels stacked top to bottom.</summary>
    public static SplitNode Rows(params LayoutNode[] children) => Layout.Rows(children);

    /// <summary>Panels placed left to right.</summary>
    public static SplitNode Columns(params LayoutNode[] children) => Layout.Columns(children);

    /// <summary>One drawable panel with a header — the same node <see cref="Layout.Panel(string, Action{IRenderSurface})"/>
    /// builds, re-exposed so a whole layout can be written without naming the shadowed class.</summary>
    public static PanelNode Panel(string title, Action<IRenderSurface> draw) => Layout.Panel(title, draw);

    /// <summary>One drawable panel with no header.</summary>
    public static PanelNode Panel(Action<IRenderSurface> draw) => Layout.Panel(draw);

    /// <summary>Every panel in the tree, in visual order.</summary>
    public IReadOnlyList<PanelNode> Panels()
    {
        var found = new List<PanelNode>();
        if (Root is not null) Collect(Root, found);
        return found;
    }

    private static void Collect(LayoutNode node, List<PanelNode> into)
    {
        switch (node)
        {
            case PanelNode panel:
                into.Add(panel);
                break;
            case SplitNode split:
                foreach (var child in split.Children) Collect(child, into);
                break;
        }
    }

    /// <summary>A split with no children lays out nothing, and a panel with no callback paints
    /// nothing. Both are author mistakes that would render as an empty window with no explanation, so
    /// the whole layout is refused and the unit falls back to its single panel.</summary>
    private static bool IsWellFormed(LayoutNode node) => node switch
    {
        PanelNode panel => panel.Draw is not null,
        SplitNode split => split.Children is { Count: > 0 } && split.Children.All(IsWellFormed),
        _ => false,
    };
}
