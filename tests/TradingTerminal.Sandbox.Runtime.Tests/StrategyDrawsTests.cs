using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.Sandbox.Runtime.Tests;

/// <summary>
/// A strategy paints.
///
/// <para><c>IStrategyKernel.Draw</c> and <c>IVisualizer.Draw</c> are the same method with the same
/// contract, and the shared window (<c>AuthoredUnitView</c>) is built on the premise that a strategy
/// IS a visualizer that can also trade. But <c>SandboxStrategyRuntime</c> had no paint path at all:
/// no <c>TryDraw</c>, no layout accessor, no draw gate. An authored strategy could draw the signal it
/// acted on and the picture went nowhere.</para>
///
/// <para>The gate is the part worth pinning. It spans the whole consistency window — deliver,
/// reconcile, commit — not just the callback, because a frame taken between the kernel writing its
/// state and the account committing paints a chart that disagrees with the book.</para>
/// </summary>
public sealed class StrategyDrawsTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly InstrumentId Instrument = new(1);

    [Fact]
    public async Task A_running_strategy_describes_its_frame()
    {
        var kernel = new DrawingKernel();
        await using var runtime = Runtime(() => kernel, kernel.Schema);
        await runtime.RunAsync();

        var surface = new RecordingRenderSurface();

        Assert.True(runtime.TryDraw(surface));
        Assert.False(surface.IsBlank);
        Assert.Contains("Signal", surface.Panels);

        await runtime.StopAsync();
    }

    [Fact]
    public void A_strategy_that_is_not_running_paints_nothing()
    {
        var kernel = new DrawingKernel();
        using var runtime = Runtime(() => kernel, kernel.Schema);

        var surface = new RecordingRenderSurface();

        Assert.False(runtime.TryDraw(surface));
        Assert.True(surface.IsBlank);
    }

    [Fact]
    public async Task A_kernel_that_throws_while_drawing_loses_its_picture_not_its_window()
    {
        // The position and the runtime survive; only the frame is lost.
        var kernel = new ThrowingDrawKernel();
        await using var runtime = Runtime(() => kernel, kernel.Schema);
        await runtime.RunAsync();

        Assert.False(runtime.TryDraw(new RecordingRenderSurface()));
        Assert.True(runtime.IsRunning, "a bad frame must not stop the strategy");

        await runtime.StopAsync();
    }

    [Fact]
    public async Task A_declared_layout_reaches_the_host_with_its_panels_gated()
    {
        var kernel = new TwoPanelKernel();
        await using var runtime = Runtime(() => kernel, kernel.Schema);
        await runtime.RunAsync();

        var layout = runtime.GetLayout();
        Assert.False(layout.IsSingle);
        Assert.Equal(["Signal", "Book"], layout.Panels().Select(p => p.Title));

        foreach (var panel in layout.Panels())
        {
            var surface = new RecordingRenderSurface();
            panel.Draw(surface);
            Assert.False(surface.IsBlank, $"panel '{panel.Title}' painted nothing");
        }

        await runtime.StopAsync();
    }

    [Fact]
    public async Task A_strategy_declaring_no_layout_gets_the_single_panel_default()
    {
        var kernel = new DrawingKernel();
        await using var runtime = Runtime(() => kernel, kernel.Schema);
        await runtime.RunAsync();

        Assert.True(runtime.GetLayout().IsSingle);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task Panels_captured_before_a_stop_are_inert_afterwards()
    {
        var kernel = new TwoPanelKernel();
        await using var runtime = Runtime(() => kernel, kernel.Schema);
        await runtime.RunAsync();
        var panels = runtime.GetLayout().Panels();
        await runtime.StopAsync();

        var surface = new RecordingRenderSurface();
        foreach (var panel in panels) panel.Draw(surface);

        Assert.True(surface.IsBlank);
    }

    private static SandboxStrategyRuntime Runtime(
        Func<IStrategyKernel> factory, StrategyParameterSchema schema) =>
        new(factory,
            schema,
            currentValues: null,
            new SilentHub(),
            new FixedClock(Epoch),
            instruments => new ModelPortfolioAccount(instruments),
            (_, _, _) => { },
            _ => { });

    // ── kernels ─────────────────────────────────────────────────────────────────────────────────

    private static StrategyParameterSchema OneInstrument() => new(
        StrategyParameter.Instrument("instrument", "Instrument", Instrument));

    private sealed class DrawingKernel : IStrategyKernel
    {
        public StrategyParameterSchema Schema { get; } = OneInstrument();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface)
        {
            using var panel = surface.Panel("Signal", RenderPanelKind.Chart);
            using var series = surface.Series("Edge", RenderSeriesKind.Line);
            surface.Push(0d, 1d);
            surface.Push(1d, 2d);
        }
    }

    private sealed class ThrowingDrawKernel : IStrategyKernel
    {
        public StrategyParameterSchema Schema { get; } = OneInstrument();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface) => throw new InvalidOperationException("bad frame");
    }

    private sealed class TwoPanelKernel : IStrategyKernel
    {
        public StrategyParameterSchema Schema { get; } = OneInstrument();

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public DaxAlgo.Sdk.Layout.UnitLayout Layout => DaxAlgo.Sdk.Layout.UnitLayout.Of(
            DaxAlgo.Sdk.Layout.Layout.Rows(
                DaxAlgo.Sdk.Layout.Layout.Panel("Signal", DrawSignal).Star(3),
                DaxAlgo.Sdk.Layout.Layout.Panel("Book", DrawBook).Pixels(120)));

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) => Task.CompletedTask;

        private static void DrawSignal(IRenderSurface surface)
        {
            using var series = surface.Series("Edge", RenderSeriesKind.Line);
            surface.Push(0d, 1d);
        }

        private static void DrawBook(IRenderSurface surface)
        {
            using var series = surface.Series("Position", RenderSeriesKind.Line);
            surface.Push(0d, 0d);
        }
    }

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────

    private sealed class SilentHub : IMarketDataHub
    {
        public IObservable<Quote> Quotes(InstrumentId instrumentId) => new Never<Quote>();

        public IObservable<TradePrint> Trades(InstrumentId instrumentId) => new Never<TradePrint>();

        public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) => new Never<OhlcvBar>();

        public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) => new Never<DepthSnapshot>();

        public void PublishQuote(Quote quote) { }

        public void PublishTrade(TradePrint trade) { }

        public void PublishBar(OhlcvBar bar) { }

        public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) { }

        private sealed class Never<T> : IObservable<T>
        {
            public IDisposable Subscribe(IObserver<T> observer) => new NoSubscription();

            private sealed class NoSubscription : IDisposable
            {
                public void Dispose() { }
            }
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
