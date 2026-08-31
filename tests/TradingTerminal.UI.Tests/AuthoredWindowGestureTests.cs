using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Sandbox.Samples;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Gestures in a REAL authored window — several panels, headers, splitters — rather than on a bare
/// surface.
///
/// <para>The gesture work was asserted on one <see cref="RenderSurfaceView"/> in isolation. That is
/// the component, not the path: a body declared as a <c>UnitLayout</c> is built by
/// <see cref="AuthoredUnitLayoutHost"/> into N surfaces, each wrapped in a grid with a header and
/// separated by splitters, and every one of those is somewhere a click can go astray. The skill for
/// this area records two bugs of exactly that shape already — a theme applied to the first surface
/// only, and an invalidation applied to the first surface only.</para>
///
/// <para>So these drive the host the way the window does, and check the two things that matter for a
/// pinned price level: that the gesture arrives at all, and that it arrives at the panel the pointer
/// was actually over.</para>
/// </summary>
public sealed class AuthoredWindowGestureTests
{
    [WpfFact]
    public void A_click_reaches_the_panel_it_landed_on_and_no_other()
    {
        // The order-book shape: a tall chart with a strip beneath it. Pinning a level on the chart
        // must not pin one on the strip, and must not be lost between them.
        var seen = new Dictionary<string, RenderCursor>();
        var host = Host(UnitLayout.Rows(
            UnitLayout.Panel("Liquidity", s => seen["Liquidity"] = s.Cursor).Star(3),
            UnitLayout.Panel("Microstructure", s => seen["Microstructure"] = s.Cursor).Star(1)));

        var surfaces = Surfaces(host);
        Assert.Equal(2, surfaces.Count);

        Click(surfaces[0], new Point(40d, 30d));
        Render(host);

        Assert.True(seen["Liquidity"].HasSelection, "the pin never reached the panel it was made on");
        Assert.False(seen["Microstructure"].HasSelection, "the other panel claimed a pin it never got");
    }

    [WpfFact]
    public void Each_panel_keeps_its_own_zoom()
    {
        // Per surface, and each panel of a UnitLayout is its own surface. Zooming the heatmap must not
        // silently rescale the ladder beside it, which is showing a different quantity entirely.
        var seen = new Dictionary<string, RenderViewport>();
        var host = Host(UnitLayout.Columns(
            UnitLayout.Panel("Chart", s => seen["Chart"] = s.Viewport).Star(3),
            UnitLayout.Panel("Book", s => seen["Book"] = s.Viewport).Pixels(120)));

        var surfaces = Surfaces(host);
        surfaces[0].Wheel(120);
        Render(host);

        Assert.True(seen["Chart"].Zoom > 1d, "the wheel never reached the panel it was turned over");
        Assert.Equal(1d, seen["Book"].Zoom);
    }

    [WpfFact]
    public void A_single_panel_body_gets_the_gestures_too()
    {
        // The default path almost every unit takes: no layout at all, one surface filling the body.
        // It goes through a different branch of Rebuild, so it is worth its own assertion.
        RenderCursor cursor = default;
        var host = new AuthoredUnitLayoutHost { Draw = s => cursor = s.Cursor };
        Render(host);

        var surfaces = Surfaces(host);
        Assert.Single(surfaces);

        Click(surfaces[0], new Point(25d, 25d));
        Render(host);

        Assert.True(cursor.HasSelection);
    }

    [WpfFact]
    public void Rebuilding_the_body_does_not_carry_a_stale_pin_into_the_new_panels()
    {
        // A unit may change its layout — the host rebuilds the surfaces when it does. The old
        // surfaces go with it, and a pin belongs to the surface it was made on, so the new body starts
        // clean rather than showing a highlight nobody placed there.
        var host = Host(UnitLayout.Rows(
            UnitLayout.Panel("A", _ => { }),
            UnitLayout.Panel("B", _ => { })));

        Click(Surfaces(host)[0], new Point(20d, 20d));

        RenderCursor after = default;
        host.Layout = UnitLayout.Rows(
            UnitLayout.Panel("C", s => after = s.Cursor),
            UnitLayout.Panel("D", _ => { }));
        Render(host);

        Assert.False(after.HasSelection);
    }

    [WpfFact]
    public void The_benchmark_window_paints_in_every_panel_after_a_real_drive()
    {
        // The closest thing to running it that does not need a provider. LiquidityBookVisualizer is
        // the goal loop's control - a hand-written authored answer to the same brief as the
        // TradingTerminal.OrderBook window - and this drives it through its real lifecycle with depth
        // and a tape, then builds the three-panel window it declares and paints it.
        //
        // Per panel deliberately. The skill for this area records that blankness has to be probed per
        // panel, because a unit with a layout leaves its own Draw empty and the panels do the
        // painting: a whole-window check passes while one panel is blank.
        var unit = new LiquidityBookVisualizer();
        SyntheticDrive.Run(unit);

        var host = Host(unit.Layout);
        var surfaces = Surfaces(host);

        Assert.Equal(3, surfaces.Count);
        for (var index = 0; index < surfaces.Count; index++)
        {
            Assert.True(
                surfaces[index].LastFrameOperationCount > 0,
                $"panel {index} of the benchmark window painted nothing after a full drive");
        }
    }

    [WpfFact]
    public void Pinning_a_level_on_the_benchmark_window_changes_what_it_paints()
    {
        // The benchmark delta claims selection is closed. This is what that has to mean: not that a
        // flag is readable, but that clicking the liquidity panel puts a line and a price on the
        // picture the user is looking at.
        var unit = new LiquidityBookVisualizer();
        SyntheticDrive.Run(unit);

        // A window-sized host, not the small default. The control gives its book panel a fixed 240px,
        // so at 320 wide the liquidity panel is 76 across and a click lands wherever geometry puts it
        // rather than where the test meant. Sizing it like a real window is the difference between
        // asserting on the picture and asserting on an accident.
        var host = Host(unit.Layout, width: 1000d, height: 700d);
        var liquidity = Surfaces(host)[0];
        var before = liquidity.LastFrameOperationCount;

        // Upper-left quadrant of the panel: inside the heat plot, clear of the imbalance lane along
        // the bottom.
        Click(liquidity, new Point(liquidity.ActualWidth * 0.25d, liquidity.ActualHeight * 0.25d));
        Render(host, 1000d, 700d);

        Assert.True(
            liquidity.LastFrameOperationCount > before,
            "the pinned level drew nothing — selection reaches the unit but not the picture");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private const double Width = 320d;
    private const double Height = 200d;

    private static AuthoredUnitLayoutHost Host(
        UnitLayout layout, double width = Width, double height = Height)
    {
        var host = new AuthoredUnitLayoutHost { Layout = layout };
        Render(host, width, height);
        return host;
    }

    /// <summary>A press and release that does not travel — a click, which is what pins a level.</summary>
    private static void Click(RenderSurfaceView surface, Point at)
    {
        surface.PressAt(at);
        surface.ReleaseAt(at);
    }

    private static void Render(FrameworkElement element, double width = Width, double height = Height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    /// <summary>Every surface the host built, in visual-tree order — the panels as the window has
    /// them, not as a test constructed them.</summary>
    private static IReadOnlyList<RenderSurfaceView> Surfaces(DependencyObject root)
    {
        var found = new List<RenderSurfaceView>();
        Walk(root, found);
        return found;
    }

    private static void Walk(DependencyObject node, List<RenderSurfaceView> found)
    {
        if (node is RenderSurfaceView surface) found.Add(surface);

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
            Walk(VisualTreeHelper.GetChild(node, index), found);
    }
}
