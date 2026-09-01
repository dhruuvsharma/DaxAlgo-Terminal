using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
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
    public void DrawIsOptional_ForBothKindsOfAuthoredUnit()
    {
        // Drawing is a hook on the unit, not a capability on the context: it runs on the RENDER
        // thread when the host paints, while the data callbacks run on a pump thread at tick rate.
        // Defaulting to nothing keeps a pure signal strategy and a headless visualizer valid.
        IVisualizer visualizer = new SilentVisualizer();
        IStrategyKernel kernel = new SilentKernel();

        var visualizerFault = Record.Exception(() => visualizer.Draw(NullRenderSurface.Instance));
        var kernelFault = Record.Exception(() => kernel.Draw(NullRenderSurface.Instance));

        Assert.Null(visualizerFault);
        Assert.Null(kernelFault);
    }

    [Fact]
    public void AStrategyDrawsThroughTheSameSurfaceAsAVisualizer()
    {
        // The point of the shared contract: a strategy's picture and a visualizer's picture are the
        // same kind of thing, so one renderer and one set of drawing routines serve both. What makes
        // a strategy different is its virtual book, not how it draws.
        var recorder = new CountingSurface();

        new SilentKernel { Frame = surface => surface.Rect(0d, 0d, 1d, 1d) }.Draw(recorder);
        new SilentVisualizer { Frame = surface => surface.Rect(0d, 0d, 1d, 1d) }.Draw(recorder);

        Assert.Equal(2, recorder.Rectangles);
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

    private sealed class SilentVisualizer : IVisualizer
    {
        internal Action<IRenderSurface>? Frame { get; init; }

        public StrategyParameterSchema Schema => throw new NotSupportedException();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.None;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface) => Frame?.Invoke(surface);
    }

    private sealed class SilentKernel : IStrategyKernel
    {
        internal Action<IRenderSurface>? Frame { get; init; }

        public StrategyParameterSchema Schema => throw new NotSupportedException();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.None;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface) => Frame?.Invoke(surface);
    }

    /// <summary>Counts what a frame asked for, without needing a window.</summary>
    private sealed class CountingSurface : IRenderSurface
    {
        internal int Rectangles { get; private set; }

        public RenderViewport Viewport => new(100d, 100d, 1d);

        public RenderCursor Cursor => new(0d, 0d, false, false);

        public DateTime Now => DateTime.MinValue;

        public RenderColor Theme(RenderThemeColor token) => new(0, 0, 0);

        public void SetStyle(RenderStyle style) { }

        public IDisposable Panel(string title, RenderPanelKind kind) => new Scope();

        public void AxisX(double minimum, double maximum, string? format = null) { }

        public void AxisY(double minimum, double maximum, string? format = null) { }

        public IDisposable Series(string name, RenderSeriesKind kind) => new Scope();

        public void Push(double x, double y) { }

        public void Line(double x1, double y1, double x2, double y2) { }

        public void Rect(double x, double y, double width, double height, bool filled = true) => Rectangles++;

        public void Text(double x, double y, string text) { }

        public void Marker(double x, double y, RenderMarkerShape shape) { }

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
