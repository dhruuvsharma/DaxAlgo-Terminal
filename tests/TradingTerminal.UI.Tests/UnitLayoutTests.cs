using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The layout vocabulary an authored unit composes its window from (issue #42).
///
/// <para>The window's chrome — parameter expander above, activity log below — is host-owned and
/// identical for every unit. This tree describes only the body between them, and it is <b>untrusted
/// input</b>: it arrives from compiled code an author or a model wrote. So the interesting cases here
/// are the malformed ones, and the rule throughout is that a bad layout costs the author their panel
/// arrangement and never their window.</para>
/// </summary>
public sealed class UnitLayoutTests
{
    private static void Nothing(IRenderSurface surface)
    {
    }

    private static PanelNode Panel(string title = "P") => Layout.Panel(title, Nothing);

    // ── the shapes issue #42 asks for ───────────────────────────────────────────────────────────

    [Fact]
    public void Two_charts_side_by_side()
    {
        var layout = UnitLayout.Of(Layout.Columns(Panel("Left"), Panel("Right")));

        Assert.False(layout.IsSingle);
        Assert.Equal(2, layout.Root!.PanelCount);
        Assert.Equal(["Left", "Right"], layout.Panels().Select(p => p.Title));
    }

    [Fact]
    public void Two_order_books_with_an_arbitrage_strip_between_them()
    {
        // The example from the issue, and the reason the vocabulary is a tree rather than a list.
        var layout = UnitLayout.Of(Layout.Columns(
            Panel("Venue A"),
            Panel("Spread").Pixels(160d),
            Panel("Venue B")));

        Assert.Equal(3, layout.Root!.PanelCount);
        Assert.Equal(
            new PanelSize(160d, PanelSizeUnit.Pixels),
            ((SplitNode)layout.Root).Children[1].Size);
    }

    [Fact]
    public void Rows_and_columns_nest()
    {
        var layout = UnitLayout.Of(Layout.Rows(
            Panel("Price").Star(3d),
            Layout.Columns(Panel("Book"), Panel("Tape"))));

        Assert.Equal(3, layout.Root!.PanelCount);

        // Depth counts the panel itself: panel = 1, the inner Columns = 2, the outer Rows = 3.
        Assert.Equal(3, layout.Root.Depth);
        Assert.Equal(["Price", "Book", "Tape"], layout.Panels().Select(p => p.Title));
    }

    [Fact]
    public void Panels_come_back_in_visual_order()
    {
        // Order is what the host lays out by, so it has to be the order the author wrote — not
        // whatever a recursive walk happens to produce.
        var layout = UnitLayout.Of(Layout.Rows(
            Layout.Columns(Panel("1"), Panel("2")),
            Layout.Columns(Panel("3"), Panel("4"))));

        Assert.Equal(["1", "2", "3", "4"], layout.Panels().Select(p => p.Title));
    }

    // ── the default ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_unit_that_describes_no_layout_gets_one_panel()
    {
        // The path almost every unit takes, and it must stay a good window rather than an error.
        Assert.True(UnitLayout.Single.IsSingle);
        Assert.Null(UnitLayout.Single.Root);
        Assert.Empty(UnitLayout.Single.Panels());
        Assert.True(UnitLayout.Of(null).IsSingle);
    }

    // ── malformed input falls back rather than breaking the window ──────────────────────────────

    [Fact]
    public void An_empty_split_falls_back_to_the_single_panel()
    {
        // Lays out nothing. Rendered literally it is an empty window with no explanation, which reads
        // as a broken application rather than an authoring mistake.
        Assert.True(UnitLayout.Of(Layout.Rows()).IsSingle);
        Assert.True(UnitLayout.Of(Layout.Columns()).IsSingle);
    }

    [Fact]
    public void A_split_containing_an_empty_split_falls_back()
    {
        // The malformed node is buried, so the check has to be recursive rather than a look at the root.
        Assert.True(UnitLayout.Of(Layout.Rows(Panel(), Layout.Columns())).IsSingle);
    }

    [Fact]
    public void A_panel_with_no_draw_callback_falls_back()
    {
        Assert.True(UnitLayout.Of(new PanelNode("Ghost", null!)).IsSingle);
    }

    [Fact]
    public void Too_many_panels_is_refused_whole()
    {
        // Refused rather than truncated: half a dashboard, silently, is a worse answer than the
        // author's own single panel plus an arrangement that visibly did not apply.
        var tooMany = Enumerable.Range(0, UnitLayout.MaximumPanels + 1)
            .Select(i => (LayoutNode)Panel(i.ToString()))
            .ToArray();

        Assert.True(UnitLayout.Of(Layout.Rows(tooMany)).IsSingle);
        Assert.False(UnitLayout.Of(Layout.Rows(tooMany[..UnitLayout.MaximumPanels])).IsSingle);
    }

    [Fact]
    public void Nesting_past_the_depth_limit_is_refused()
    {
        // An authored unit is untrusted input and this is a tree it supplies; without a cap, a
        // generated layout can build a visual tree deep enough to take the window down.
        LayoutNode deep = Panel();
        for (var i = 0; i < UnitLayout.MaximumDepth + 1; i++) deep = Layout.Rows(deep);

        Assert.True(UnitLayout.Of(deep).IsSingle);
    }

    // ── sizing ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_child_fills_by_default()
    {
        Assert.Equal(PanelSize.Fill, Panel().Size);
        Assert.Equal(PanelSizeUnit.Star, PanelSize.Fill.Unit);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void A_non_positive_star_weight_becomes_one_share(double weight)
    {
        // Zero and negative weights collapse a panel to nothing in a Grid. Treating them as one share
        // keeps the panel visible, which is certainly what the author meant.
        Assert.Equal(1d, PanelSize.Star(weight).Value);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.4d)]
    [InlineData(-20d)]
    public void A_sub_pixel_fixed_extent_becomes_a_share(double extent)
    {
        // A zero-height panel is never what anyone meant, and it renders as an invisible one rather
        // than as an error.
        Assert.Equal(PanelSize.Fill, PanelSize.Pixels(extent));
    }

    [Fact]
    public void Sizes_are_applied_without_mutating_the_node()
    {
        // The nodes are records and the builder returns copies. If Star() mutated in place, a panel
        // reused across two layouts would silently take the second one's size in both.
        var original = Panel("Shared");
        var sized = original.Star(4d);

        Assert.Equal(PanelSize.Fill, original.Size);
        Assert.Equal(4d, sized.Size.Value);
        Assert.Equal("Shared", sized.Title);
    }
}
