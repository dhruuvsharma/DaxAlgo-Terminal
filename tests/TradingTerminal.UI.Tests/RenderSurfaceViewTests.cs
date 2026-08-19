using System.Windows;
using System.Windows.Media;
using DaxAlgo.Sdk;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The renderer that turns <see cref="IRenderSurface"/> calls into pixels — the substrate every
/// richer control stands on. These assert the behaviours a visualizer author depends on and the
/// bounds an untrusted one is held to.
/// </summary>
public sealed class RenderSurfaceViewTests
{
    [WpfFact]
    public void AFrameThatDrawsNothing_CostsNothing()
    {
        var view = Arrange(_ => { });

        Render(view);

        Assert.Equal(0, view.LastFrameOperationCount);
        Assert.False(view.LastFrameWasTruncated);
    }

    [WpfFact]
    public void PrimitivesAreCounted()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                surface.Line(0d, 0d, 10d, 10d);
                surface.Rect(0d, 0d, 5d, 5d);
                surface.Text(0d, 0d, "x");
                surface.Marker(1d, 1d, RenderMarkerShape.Circle);
            }
        });

        Render(view);

        // Four primitives plus the panel itself.
        Assert.Equal(5, view.LastFrameOperationCount);
        Assert.False(view.LastFrameWasTruncated);
    }

    [WpfFact]
    public void ARunawayVisualizer_IsTruncatedRatherThanAllowedToWedgeTheThread()
    {
        // The whole point of a bounded surface: untrusted code gets cut off, not trusted to stop.
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                for (var index = 0; index < RenderSurfaceView.MaximumOperationsPerFrame * 2; index++)
                    surface.Line(0d, 0d, 1d, 1d);
            }
        });

        Render(view);

        Assert.True(view.LastFrameWasTruncated);
        Assert.Equal(RenderSurfaceView.MaximumOperationsPerFrame, view.LastFrameOperationCount);
    }

    [WpfFact]
    public void AVisualizerThatThrowsMidFrame_DoesNotTakeTheWindowDown()
    {
        // A partial frame is a far better outcome than an unhandled exception on the render thread;
        // the runtime that owns the visualizer is what reports the fault.
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                surface.Line(0d, 0d, 1d, 1d);
                throw new InvalidOperationException("visualizer blew up");
            }
        });

        var exception = Record.Exception(() => Render(view));

        Assert.Null(exception);
    }

    [WpfFact]
    public void ViewportReportsThePanelArea_NotTheWholeControl()
    {
        RenderViewport outer = default;
        RenderViewport inner = default;
        var view = Arrange(surface =>
        {
            outer = surface.Viewport;
            using (surface.Panel("a", RenderPanelKind.Chart))
                inner = surface.Viewport;
        });

        Render(view, width: 400d, height: 300d);

        Assert.Equal(400d, outer.Width);
        Assert.Equal(300d, outer.Height);
        // A single panel owns the whole control.
        Assert.Equal(400d, inner.Width);
        Assert.Equal(300d, inner.Height);
    }

    [WpfFact]
    public void TwoPanels_SplitTheHeight()
    {
        var heights = new List<double>();
        var view = Arrange(surface =>
        {
            using (surface.Panel("a", RenderPanelKind.Chart))
                heights.Add(surface.Viewport.Height);
            using (surface.Panel("b", RenderPanelKind.Ladder))
                heights.Add(surface.Viewport.Height);
        });

        Render(view, width: 400d, height: 300d);

        // BOTH panels get half. The discovery pass is what makes this true: without it the first
        // panel is laid out before the frame knows a second one is coming, and is drawn at full
        // height every frame.
        //
        // Asserted on the last two entries rather than the count, because WPF may run more than one
        // render pass and Draw is documented as pure for exactly that reason.
        Assert.True(heights.Count >= 2);
        Assert.Equal(150d, heights[^2]);
        Assert.Equal(150d, heights[^1]);
    }

    [WpfFact]
    public void CursorIsAbsentWhenThePointerHasNotEnteredTheControl()
    {
        RenderCursor cursor = default;
        var view = Arrange(surface =>
        {
            using (surface.Panel("a", RenderPanelKind.Chart))
                cursor = surface.Cursor;
        });

        Render(view);

        Assert.False(cursor.IsInside);
        Assert.False(cursor.IsPressed);
    }

    [WpfFact]
    public void SeriesScopeClosesEvenIfTheVisualizerForgets()
    {
        // A visualizer that never disposes its scopes must not corrupt the drawing context for the
        // rest of the frame, so the renderer balances what was left open.
        var view = Arrange(surface =>
        {
            surface.Panel("a", RenderPanelKind.Chart);
            surface.Series("s", RenderSeriesKind.Line);
            surface.Push(0d, 0d);
            surface.Push(1d, 1d);
        });

        var exception = Record.Exception(() => Render(view));

        Assert.Null(exception);
    }

    [WpfFact]
    public void ThemeRolesResolveThroughTheHostResolver()
    {
        RenderColor resolved = default;
        var view = Arrange(surface =>
        {
            using (surface.Panel("a", RenderPanelKind.Chart))
                resolved = surface.Theme(RenderThemeColor.Bullish);
        });
        view.ThemeResolver = _ => Color.FromRgb(1, 2, 3);

        Render(view);

        // The host owns the palette; a visualizer names a role and gets whatever the theme is using.
        Assert.Equal(new RenderColor(1, 2, 3), resolved);
    }

    private static RenderSurfaceView Arrange(Action<IRenderSurface> draw) => new() { Draw = draw };

    private static void Render(RenderSurfaceView view, double width = 200d, double height = 100d)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0d, 0d, width, height));

        // Force a synchronous render pass: OnRender is protected, so drive it the way WPF would.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            typeof(RenderSurfaceView)
                .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(view, [context]);
        }
    }
}
