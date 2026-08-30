using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using SdkLayout = DaxAlgo.Sdk.Layout.Layout;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;
using Xunit;

namespace TradingTerminal.Sandbox.Runtime.Tests;

/// <summary>
/// A unit's declared window shape has to reach the window.
///
/// <para>It did not. <c>IVisualizer.Layout</c> was declared, bounded, taught to Hyperion, and rendered
/// by a purpose-built control with draggable separators — and <b>nothing ever read it for a running
/// unit</b>. The runtime did not expose it, the host never asked, and
/// <c>AuthoredUnitPresenter.Layout</c> stayed at <c>Single</c> for the life of every window. The
/// preview showed the panels; the window that opened afterwards showed one. Ask for two order books
/// with a spread strip between them, watch it appear in the preview, register it, open it — one
/// panel.</para>
///
/// <para>The other half is why the tree cannot simply be handed over: a <c>PanelNode</c> carries a
/// callback closing over the visualizer instance, and the layout host invokes it straight from the
/// render thread. Passing the raw tree would run author code outside the draw gate, concurrently with
/// a market-data callback mutating the same state — the exact hazard <c>TryDraw</c> exists to prevent.</para>
/// </summary>
public sealed class VisualizerLayoutReachesTheWindowTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_declared_layout_is_reported_by_the_running_runtime()
    {
        await using var runtime = Runtime(() => new TwoPanelVisualizer());
        await runtime.StartAsync();

        var layout = runtime.GetLayout();

        Assert.False(layout.IsSingle, "the unit declared two panels");
        Assert.Equal(2, layout.Panels().Count);
        Assert.Equal(["Price", "Book"], layout.Panels().Select(p => p.Title));

        await runtime.StopAsync();
    }

    [Fact]
    public async Task The_panels_handed_out_actually_draw()
    {
        // A layout of panels that paint nothing is the same blank window with extra headers.
        await using var runtime = Runtime(() => new TwoPanelVisualizer());
        await runtime.StartAsync();

        // Each panel is checked on its OWN surface, because that is how it is hosted: the layout host
        // gives every panel a surface of its own, and opens the header itself — which is why a panel
        // callback does not call surface.Panel() and its title comes from the node, not the drawing.
        foreach (var panel in runtime.GetLayout().Panels())
        {
            var surface = new RecordingRenderSurface();
            panel.Draw(surface);

            Assert.False(surface.IsBlank, $"panel '{panel.Title}' painted nothing");
            Assert.NotEmpty(surface.Points);
        }

        await runtime.StopAsync();
    }

    [Fact]
    public async Task A_panel_callback_is_inert_once_the_unit_stops()
    {
        // The callbacks outlive the window's own teardown ordering, so they have to be safe to call
        // against a runtime that has stopped rather than painting from a torn-down session.
        await using var runtime = Runtime(() => new TwoPanelVisualizer());
        await runtime.StartAsync();
        var panels = runtime.GetLayout().Panels();
        await runtime.StopAsync();

        var surface = new RecordingRenderSurface();
        foreach (var panel in panels) panel.Draw(surface);

        Assert.True(surface.IsBlank, "a stopped unit paints nothing rather than throwing");
    }

    [Fact]
    public async Task A_unit_that_declares_nothing_gets_the_single_panel_default()
    {
        await using var runtime = Runtime(() => new PlainVisualizer());
        await runtime.StartAsync();

        Assert.True(runtime.GetLayout().IsSingle);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task A_unit_that_throws_describing_its_window_keeps_one()
    {
        // Reading Layout runs author code. Losing the panel arrangement is a fair price; losing the
        // window is not.
        await using var runtime = Runtime(() => new ThrowingLayoutVisualizer());
        await runtime.StartAsync();

        Assert.True(runtime.GetLayout().IsSingle);

        await runtime.StopAsync();
    }

    [Fact]
    public void An_unstarted_runtime_reports_the_default_rather_than_reaching_for_a_unit()
    {
        using var runtime = Runtime(() => new TwoPanelVisualizer());

        Assert.True(runtime.GetLayout().IsSingle);
    }

    [Fact]
    public async Task Resuming_hands_out_panels_bound_to_the_NEW_session()
    {
        // Resume tears the session down and builds another. Callbacks captured before it must not go
        // on painting the instance that was disposed — which is why they re-read the live session
        // rather than closing over one.
        await using var runtime = Runtime(() => new TwoPanelVisualizer());
        await runtime.StartAsync();

        var beforePanels = runtime.GetLayout().Panels();
        await runtime.PauseAsync();
        await runtime.ResumeAsync();

        var stale = new RecordingRenderSurface();
        foreach (var panel in beforePanels) panel.Draw(stale);

        // The old handles still work — they resolve the CURRENT session — so a window that kept them
        // paints the running unit rather than nothing.
        Assert.False(stale.IsBlank);

        await runtime.StopAsync();
    }

    private static SandboxVisualizerRuntime Runtime(Func<IVisualizer> factory) =>
        new(factory,
            currentValues: null,
            new SilentHub(),
            new FixedClock(Epoch),
            (_, _, _) => { },
            _ => { });

    /// <summary>A hub that never publishes. These tests are about the window's shape, which a unit
    /// declares without having seen a tick.</summary>
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

    // ── units ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A chart beside a book — the arrangement issue #42 opens with.</summary>
    private sealed class TwoPanelVisualizer : IVisualizer
    {
        // The runtime refuses a unit with no instrument to bind a feed to, so every unit here declares
        // one even though these tests never publish a tick.
        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)));

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public UnitLayout Layout => UnitLayout.Of(SdkLayout.Columns(
            SdkLayout.Panel("Price", DrawPrice).Star(3),
            SdkLayout.Panel("Book", DrawBook).Pixels(260)));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        private static void DrawPrice(IRenderSurface surface)
        {
            using var series = surface.Series("Price", RenderSeriesKind.Line);
            surface.Push(0d, 1d);
            surface.Push(1d, 2d);
        }

        private static void DrawBook(IRenderSurface surface)
        {
            using var series = surface.Series("Depth", RenderSeriesKind.Line);
            surface.Push(0d, 1d);
        }
    }

    private sealed class PlainVisualizer : IVisualizer
    {
        // The runtime refuses a unit with no instrument to bind a feed to, so every unit here declares
        // one even though these tests never publish a tick.
        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)));

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingLayoutVisualizer : IVisualizer
    {
        // The runtime refuses a unit with no instrument to bind a feed to, so every unit here declares
        // one even though these tests never publish a tick.
        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)));

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public UnitLayout Layout => throw new InvalidOperationException("bad layout");

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
    }
}
