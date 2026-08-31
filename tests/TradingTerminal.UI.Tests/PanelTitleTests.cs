using System.Windows;
using System.Windows.Media;
using DaxAlgo.Sdk;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The title a unit gives a panel, which was passed, stored and drawn by nothing.
///
/// <para>A body declared as a <c>UnitLayout</c> gets a real header per panel from the layout host. A
/// unit that instead divides ONE surface with several <c>Panel</c> scopes — which is what both
/// exemplars do, and therefore what a generated unit copies — got unlabelled regions, and the title
/// argument it had dutifully supplied went nowhere.</para>
///
/// <para>Found by checking the three things a panel scope carries: title, kind, and the axis format.
/// All three were stored on the panel slot and read by no one, while the drawing pack told every model
/// that kinds "tell the host what chrome and default axes to supply". The title is the one worth
/// making true — the other two are now documented as what they are.</para>
/// </summary>
public sealed class PanelTitleTests
{
    [WpfFact]
    public void A_panel_title_is_drawn()
    {
        var view = Arrange(surface =>
        {
            using var panel = surface.Panel("Liquidity", RenderPanelKind.Chart);
            surface.Line(0d, 0d, 10d, 10d);
        });

        Assert.Contains("Liquidity", Text(view));
    }

    [WpfFact]
    public void Every_panel_of_a_stacked_body_gets_its_own()
    {
        // The case that matters: a unit dividing one surface, where nothing else labels the regions.
        var view = Arrange(surface =>
        {
            using (surface.Panel("Pressure", RenderPanelKind.Chart)) surface.Line(0d, 0d, 1d, 1d);
            using (surface.Panel("Book", RenderPanelKind.Ladder)) surface.Line(0d, 0d, 1d, 1d);
        });

        var text = Text(view);
        Assert.Contains("Pressure", text);
        Assert.Contains("Book", text);
    }

    [WpfFact]
    public void An_untitled_panel_draws_no_title()
    {
        var view = Arrange(surface =>
        {
            using var panel = surface.Panel(string.Empty, RenderPanelKind.Canvas);
            surface.Line(0d, 0d, 10d, 10d);
        });

        Assert.Empty(Text(view).Trim());
    }

    [WpfFact]
    public void The_title_is_not_charged_to_the_frame_budget()
    {
        // The budget bounds what UNTRUSTED code may emit. Charging a unit for the host's own decoration
        // would let a chrome change push a well-behaved visualizer over the limit — and it would make
        // the count mean two different things.
        var view = Arrange(surface =>
        {
            using var panel = surface.Panel("Titled", RenderPanelKind.Chart);
            surface.Line(0d, 0d, 10d, 10d);
            surface.Rect(0d, 0d, 5d, 5d);
        });

        Render(view);

        // The panel itself plus the two primitives, and nothing for the title.
        Assert.Equal(3, view.LastFrameOperationCount);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    private const double Width = 240d;
    private const double Height = 200d;

    private static RenderSurfaceView Arrange(Action<IRenderSurface> draw)
    {
        var view = new RenderSurfaceView { Draw = draw };
        Render(view);
        return view;
    }

    private static void Render(RenderSurfaceView view)
    {
        view.Measure(new Size(Width, Height));
        view.Arrange(new Rect(0d, 0d, Width, Height));

        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        typeof(RenderSurfaceView)
            .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [context]);
    }

    /// <summary>Everything the frame actually painted as text, read back off the drawing instructions
    /// rather than from the surface — a title the unit never asked for has to be visible to count.</summary>
    private static string Text(RenderSurfaceView view)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0d, 0d, Width, Height));
            typeof(RenderSurfaceView)
                .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(view, [context]);
        }

        var found = new System.Text.StringBuilder();
        Walk(visual.Drawing, found);
        return found.ToString();
    }

    private static void Walk(Drawing? drawing, System.Text.StringBuilder found)
    {
        switch (drawing)
        {
            case GlyphRunDrawing glyphs when glyphs.GlyphRun is { } run:
                found.Append(run.Characters is { Count: > 0 }
                    ? new string([.. run.Characters])
                    : string.Empty);
                break;

            case DrawingGroup group:
                foreach (var child in group.Children) Walk(child, found);
                break;
        }
    }
}
