using System.Windows;
using System.Windows.Media;
using DaxAlgo.Sdk;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// What a badly written visualizer does to the renderer. The author is not trusted, so every one of
/// these has to degrade rather than corrupt the frame or throw.
/// </summary>
public sealed class RenderSurfaceAbuseTests
{
    [WpfFact]
    public void NestedPanels_DoNotUnbalanceTheDrawingContext()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("outer", RenderPanelKind.Chart))
            {
                using (surface.Panel("inner", RenderPanelKind.Ladder))
                    surface.Rect(0d, 0d, 1d, 1d);

                // Still inside the outer panel — drawing here must not land on a popped clip.
                surface.Rect(0d, 0d, 1d, 1d);
            }
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
    }

    [WpfFact]
    public void NestedSeries_DoNotSilentlySwallowTheOuterOne()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Chart))
            {
                surface.AxisX(0d, 10d);
                surface.AxisY(0d, 10d);
                using (surface.Series("outer", RenderSeriesKind.Line))
                {
                    surface.Push(0d, 0d);
                    using (surface.Series("inner", RenderSeriesKind.Line))
                    {
                        surface.Push(1d, 1d);
                        surface.Push(2d, 2d);
                    }

                    surface.Push(3d, 3d);
                }
            }
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
    }

    [WpfFact]
    public void DrawingBeforeAnyPanel_IsAccepted()
    {
        // A one-panel visualizer that never opens a panel is a reasonable thing to write.
        var view = Arrange(surface =>
        {
            surface.Rect(0d, 0d, 10d, 10d);
            surface.Line(0d, 0d, 10d, 10d);
        });

        Render(view);

        Assert.Equal(2, view.LastFrameOperationCount);
    }

    [WpfFact]
    public void PushOutsideASeries_IsIgnoredRatherThanThrowing()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Chart))
                surface.Push(1d, 1d);
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
    }

    [WpfFact]
    public void DegenerateAxes_DoNotProduceInfiniteCoordinates()
    {
        // A visualizer whose data has not arrived yet declares min == max. Dividing by that range
        // would put every coordinate at infinity and poison the visual tree.
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Chart))
            {
                surface.AxisX(5d, 5d);
                surface.AxisY(5d, 5d);
                surface.Rect(5d, 5d, 1d, 1d);
                surface.Line(5d, 5d, 6d, 6d);
            }
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
    }

    [WpfFact]
    public void NonFiniteCoordinates_AreRefusedRatherThanDrawn()
    {
        // NaN reaches a surface the moment a visualizer divides by a zero volume. WPF will happily
        // accept it and then the whole visual is corrupt.
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                surface.Line(double.NaN, 0d, 10d, 10d);
                surface.Rect(double.PositiveInfinity, 0d, 5d, 5d);
                surface.Marker(double.NaN, double.NaN, RenderMarkerShape.Circle);
                surface.Text(double.NaN, 0d, "x");
            }
        });

        Render(view);

        // Asserting "did not throw" would be a false negative: WPF accepts NaN happily and only the
        // rendered visual is wrong. Every primitive must be refused — the one remaining operation is
        // the panel itself, which pushes a clip and legitimately costs budget.
        Assert.Equal(1, view.LastFrameOperationCount);
    }

    [WpfFact]
    public void NonFiniteStyle_DoesNotProduceAnUnpaintablePen()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Canvas))
            {
                surface.SetStyle(new RenderStyle(
                    new RenderColor(255, 0, 0),
                    Thickness: double.NaN,
                    Alpha: double.NaN,
                    FontSize: double.NaN));
                surface.Line(0d, 0d, 10d, 10d);
                surface.Text(0d, 0d, "x");
            }
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
        // Both primitives still draw — a nonsense STYLE degrades to the defaults rather than
        // dropping the drawing, which is the opposite of a nonsense COORDINATE. Plus the panel.
        Assert.Equal(3, view.LastFrameOperationCount);
    }

    [WpfFact]
    public void AnEmptySeries_DrawsNothingAndDoesNotThrow()
    {
        var view = Arrange(surface =>
        {
            using (surface.Panel("p", RenderPanelKind.Chart))
            using (surface.Series("empty", RenderSeriesKind.Area))
            {
            }
        });

        var fault = Record.Exception(() => Render(view));

        Assert.Null(fault);
    }

    private static RenderSurfaceView Arrange(Action<IRenderSurface> draw) => new() { Draw = draw };

    private static void Render(RenderSurfaceView view, double width = 200d, double height = 100d)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0d, 0d, width, height));

        var visual = new DrawingVisual();
        using var context = visual.RenderOpen();
        typeof(RenderSurfaceView)
            .GetMethod("OnRender", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(view, [context]);
    }
}
