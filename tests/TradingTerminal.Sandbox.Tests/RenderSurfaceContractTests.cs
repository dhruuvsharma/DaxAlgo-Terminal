using DaxAlgo.Sdk;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The render-surface contract, and the one property that makes it safe to hand to untrusted code:
/// a visualizer never has to know whether anyone is looking.
/// </summary>
public sealed class RenderSurfaceContractTests
{
    [Fact]
    public void NullSurface_AcceptsAFullFrameAndDiscardsIt()
    {
        // A visualizer written against the surface must run unchanged in a headless host. If any of
        // this threw, every visualizer would need to guard its own drawing.
        var surface = NullRenderSurface.Instance;

        using (surface.Panel("Depth", RenderPanelKind.Ladder))
        {
            surface.AxisX(0d, 10d);
            surface.AxisY(100d, 200d, "0.00");
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Bullish), Thickness: 2d));
            surface.Rect(0d, 0d, 5d, 5d);
            surface.Line(0d, 0d, 5d, 5d);
            surface.Text(1d, 1d, "bid");
            surface.Marker(2d, 2d, RenderMarkerShape.Circle);

            using (surface.Series("mid", RenderSeriesKind.Line))
            {
                surface.Push(0d, 100d);
                surface.Push(1d, 101d);
            }
        }
    }

    [Fact]
    public void NullSurface_ReportsAZeroViewportAndAnAbsentCursor()
    {
        var surface = NullRenderSurface.Instance;

        // Zero rather than an invented size: a visualizer that scales to the viewport then draws
        // nothing, which is the honest outcome, instead of laying out against a bogus width.
        Assert.Equal(0d, surface.Viewport.Width);
        Assert.Equal(0d, surface.Viewport.Height);
        Assert.Equal(1d, surface.Viewport.Scale);

        Assert.False(surface.Cursor.IsInside);
        Assert.False(surface.Cursor.IsPressed);
    }

    [Fact]
    public void VisualizerContext_DefaultsToTheDiscardingSurface()
    {
        // The default on IVisualizerContext is what keeps this additive: an existing host that knows
        // nothing about rendering still satisfies the interface.
        // Typed as the interface deliberately: Surface is a default interface member, so it is
        // reachable through IVisualizerContext and NOT through the implementing type. Hosts that
        // hold a concrete context will hit the same rule.
        IVisualizerContext context = new MinimalContext();

        Assert.Same(NullRenderSurface.Instance, context.Surface);
    }

    [Fact]
    public void Style_DefaultsAreOpaqueHairlineAndUndashed()
    {
        var style = new RenderStyle(new RenderColor(255, 255, 255));

        Assert.Equal(1d, style.Thickness);
        Assert.Equal(1d, style.Alpha);
        Assert.False(style.Dashed);
        Assert.Equal(11d, style.FontSize);
    }

    /// <summary>A context that implements only the members that existed before the surface was added.</summary>
    private sealed class MinimalContext : IVisualizerContext
    {
        public IMarketDataView Data => throw new NotSupportedException();

        public TradingTerminal.Core.Time.IClock Clock => throw new NotSupportedException();

        public IParameters Parameters => throw new NotSupportedException();

        public IAlertSink Alerts => throw new NotSupportedException();
    }
}
