using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The seam that makes a running visualizer visible: the host asks for a frame from the render thread
/// while the pump is delivering market data on another. These are about that collision.
/// </summary>
public sealed class SandboxVisualizerDrawTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ARunningVisualizerDraws()
    {
        var instrument = new InstrumentId(950);
        await using var runtime = Arrange(instrument, out var hub, out var visualizer);
        await runtime.StartAsync();

        var surface = new CountingSurface();
        Assert.True(runtime.TryDraw(surface));
        Assert.Equal(1, surface.Frames);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task NothingDrawsBeforeStartOrAfterStop()
    {
        // The window opens before the feed connects and stays open after it goes; painting from a
        // visualizer that was never started, or was already torn down, is how a host reads freed state.
        var instrument = new InstrumentId(951);
        await using var runtime = Arrange(instrument, out _, out _);
        var surface = new CountingSurface();

        Assert.False(runtime.TryDraw(surface));

        await runtime.StartAsync();
        Assert.True(runtime.TryDraw(surface));

        await runtime.StopAsync();
        Assert.False(runtime.TryDraw(surface));
        Assert.Equal(1, surface.Frames);
    }

    [Fact]
    public async Task AFrameIsSkippedRatherThanWaitingOnASlowVisualizer()
    {
        // The whole reason TryDraw exists. The pump holds the gate across each callback, so a
        // visualizer that blocks in OnBarAsync would freeze the UI thread if the host waited its turn.
        // A skipped frame is invisible at render cadence; a frozen window is not.
        var instrument = new InstrumentId(952);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new FakeMarketDataHub();
        var schema = InstrumentSchema(instrument);

        await using var runtime = new SandboxVisualizerRuntime(
            () => new BlockingVisualizer(schema, entered, release.Task),
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        var surface = new CountingSurface();
        bool drew;
        long skipped;
        try
        {
            hub.PublishBar(BarFor(instrument, sequence: 1, close: 100d));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            drew = runtime.TryDraw(surface);
            skipped = runtime.SkippedFrameCount;
        }
        finally
        {
            // Unparked in a finally, not after the assertions: the visualizer is deliberately blocked
            // inside its callback, so an assertion that throws first would leave teardown waiting on a
            // pump that can never finish — and the test would HANG rather than fail. Confirmed by
            // breaking the gate on purpose and watching exactly that happen.
            release.SetResult();
        }

        // The visualizer is parked inside its callback, so the frame is dropped — not queued behind it.
        Assert.False(drew);
        Assert.Equal(0, surface.Frames);
        Assert.Equal(1L, skipped);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task AVisualizerThatThrowsWhileDrawingLosesItsPictureAndNotItsWindow()
    {
        var instrument = new InstrumentId(953);
        var hub = new FakeMarketDataHub();
        var logs = new List<(string Source, string Level, string Message)>();

        await using var runtime = new SandboxVisualizerRuntime(
            () => new ThrowingDrawVisualizer(InstrumentSchema(instrument)),
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (source, level, message) =>
            {
                lock (logs) logs.Add((source, level, message));
            },
            _ => { });

        await runtime.StartAsync();

        Assert.False(runtime.TryDraw(new CountingSurface()));
        Assert.False(runtime.TryDraw(new CountingSurface()));
        Assert.False(runtime.TryDraw(new CountingSurface()));

        // Announced once, counted every time: Draw runs every frame, so repeating the alert would bury
        // the log it is meant to appear in.
        lock (logs)
        {
            Assert.Single(logs);
            Assert.Equal("Error", logs[0].Level);
            Assert.Contains("failed while drawing", logs[0].Message);
        }

        Assert.Equal(3L, runtime.DrawFaultCount);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task DrawSeesTheStateTheCallbacksBuilt()
    {
        // Compute in the data callbacks, draw from what they left behind. This is the contract
        // IVisualizer.Draw documents, and the gate is what makes a frame see a whole update rather
        // than one a handler is halfway through.
        var instrument = new InstrumentId(954);
        await using var runtime = Arrange(instrument, out var hub, out var visualizer);
        await runtime.StartAsync();

        hub.PublishBar(BarFor(instrument, sequence: 1, close: 123.5d));
        await WaitUntilAsync(() => visualizer()?.BarCount == 1);

        var surface = new CountingSurface();
        Assert.True(runtime.TryDraw(surface));

        Assert.Equal(123.5d, surface.LastValue);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task DrawingRefusesANullSurface()
    {
        await using var runtime = Arrange(new InstrumentId(955), out _, out _);

        Assert.Throws<ArgumentNullException>(() => runtime.TryDraw(null!));
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static SandboxVisualizerRuntime Arrange(
        InstrumentId instrument,
        out FakeMarketDataHub hub,
        out Func<DrawingVisualizer?> visualizer)
    {
        var schema = InstrumentSchema(instrument);
        var feed = new FakeMarketDataHub();
        DrawingVisualizer? built = null;
        hub = feed;
        visualizer = () => Volatile.Read(ref built);

        IVisualizer Build()
        {
            var instance = new DrawingVisualizer(schema);
            Volatile.Write(ref built, instance);
            return instance;
        }

        return new SandboxVisualizerRuntime(
            Build,
            currentValues: null,
            feed,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });
    }

    private static StrategyParameterSchema InstrumentSchema(InstrumentId instrument) =>
        new(StrategyParameter.Instrument("instrument", "Instrument", instrument));

    private static OhlcvBar BarFor(InstrumentId instrument, int sequence, double close) =>
        new(
            instrument,
            BarSize.OneMinute,
            Epoch.AddMinutes(sequence),
            close,
            close,
            close,
            close,
            sequence,
            BrokerKind.Simulated,
            IsFinal: true);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        Assert.Fail("The condition was not met before the deadline.");
    }

    private sealed class DrawingVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        private int _barCount;
        private double _lastClose;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public int BarCount => Volatile.Read(ref _barCount);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            Volatile.Write(ref _lastClose, bar.Close);
            Interlocked.Increment(ref _barCount);
            return Task.CompletedTask;
        }

        public void Draw(IRenderSurface surface)
        {
            using (surface.Panel("p", RenderPanelKind.Chart))
                surface.Push(0d, Volatile.Read(ref _lastClose));
        }
    }

    private sealed class BlockingVisualizer(
        StrategyParameterSchema schema,
        TaskCompletionSource entered,
        Task release) : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public async Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            entered.TrySetResult();
            await release.ConfigureAwait(false);
        }
    }

    private sealed class ThrowingDrawVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface) => throw new InvalidOperationException("bad frame");
    }

    /// <summary>Records what a frame described, so "it drew" is asserted rather than assumed.</summary>
    private sealed class CountingSurface : IRenderSurface
    {
        internal int Frames { get; private set; }

        internal double LastValue { get; private set; }

        public RenderViewport Viewport => new(400d, 300d, 1d);

        public RenderCursor Cursor => new(0d, 0d, false, false);

        public RenderColor Theme(RenderThemeColor token) => new(1, 2, 3);

        public void SetStyle(RenderStyle style) { }

        public IDisposable Panel(string title, RenderPanelKind kind)
        {
            Frames++;
            return new Scope();
        }

        public void AxisX(double minimum, double maximum, string? format = null) { }

        public void AxisY(double minimum, double maximum, string? format = null) { }

        public IDisposable Series(string name, RenderSeriesKind kind) => new Scope();

        public void Push(double x, double y) => LastValue = y;

        public void Line(double x1, double y1, double x2, double y2) { }

        public void Rect(double x, double y, double width, double height, bool filled = true) { }

        public void Text(double x, double y, string text) { }

        public void Marker(double x, double y, RenderMarkerShape shape) { }

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
