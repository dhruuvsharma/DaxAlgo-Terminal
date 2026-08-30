using System.Collections.Concurrent;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Sandbox;
using Xunit;

namespace DaxAlgo.Sandbox.Samples.Tests;

public sealed class SandboxSampleTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MovingAverageCrossSubmitsLongThenFlatTargetsAndMediatesAlerts()
    {
        var instrument = new InstrumentId(7101);
        var kernel = new MovingAverageCrossKernel();
        var hub = new InMemoryMarketDataHub();
        var clock = new TestClock(Epoch);
        var logs = new List<(string Source, string Level, string Message)>();
        var banners = new List<AlertRecord>();
        var book = new RecordingVirtualBook();
        var parameters = new SandboxParameters(
            kernel.Schema,
            new Dictionary<string, object?>
            {
                [MovingAverageCrossKernel.InstrumentParameter] = instrument,
                [MovingAverageCrossKernel.FastPeriodParameter] = 2,
                [MovingAverageCrossKernel.SlowPeriodParameter] = 3,
                [MovingAverageCrossKernel.UseProtectiveStopParameter] = true,
                [MovingAverageCrossKernel.ProtectiveStopPercentParameter] = 5d,
            });
        using var data = new ScopedMarketDataView(
            new HashSet<InstrumentId> { instrument },
            kernel.DataRequirement,
            hub,
            retentionBound: 16);
        var alerts = new MediatedAlertSink(
            nameof(MovingAverageCrossKernel),
            clock,
            (source, level, message) => logs.Add((source, level, message)),
            banners.Add);
        var context = new TestStrategyRuntimeContext(data, clock, parameters, book, alerts);

        await kernel.OnStartAsync(context, CancellationToken.None);

        var closes = new[] { 3d, 2d, 1d, 4d, 0d, 0d };
        for (var index = 0; index < closes.Length; index++)
        {
            var bar = BarFor(instrument, index, closes[index]);
            hub.PublishBar(bar);
            await kernel.OnBarAsync(bar, context, CancellationToken.None);
        }

        await ((IStrategyKernel)kernel).OnStopAsync(context, CancellationToken.None);

        Assert.Collection(
            book.Intents,
            longIntent =>
            {
                Assert.Equal(instrument, longIntent.Instrument);
                Assert.Equal(1d, longIntent.TargetUnits);
                Assert.NotNull(longIntent.ProtectiveStopPrice);
                Assert.Equal(3.8d, longIntent.ProtectiveStopPrice.Value, precision: 10);
                Assert.Null(longIntent.ProfitTargetPrice);
            },
            flatIntent =>
            {
                Assert.Equal(instrument, flatIntent.Instrument);
                Assert.Equal(0d, flatIntent.TargetUnits);
                Assert.Null(flatIntent.ProtectiveStopPrice);
                Assert.Null(flatIntent.ProfitTargetPrice);
            });
        Assert.Equal(2, banners.Count);
        Assert.All(banners, alert => Assert.Equal(AlertLevel.Information, alert.Level));
        Assert.Contains(banners, alert => alert.Message.Contains("long", StringComparison.Ordinal));
        Assert.Contains(banners, alert => alert.Message.Contains("flat", StringComparison.Ordinal));
        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.Equal("INFO", log.Level));
    }

    [Fact]
    public async Task SpreadBandVisualizerAutoRunsUpdatesStateAndMediatesOutsideBandAlert()
    {
        var instrument = new InstrumentId(7201);
        var hub = new InMemoryMarketDataHub();
        var clock = new TestClock(Epoch);
        var logs = new ConcurrentQueue<(string Source, string Level, string Message)>();
        var banners = new ConcurrentQueue<AlertRecord>();
        SpreadBandVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new SpreadBandVisualizer(),
            new Dictionary<string, object?>
            {
                [SpreadBandVisualizer.InstrumentParameter] = instrument,
                [SpreadBandVisualizer.LookbackParameter] = 3,
                [SpreadBandVisualizer.BandMultiplierParameter] = 2d,
            },
            hub,
            clock,
            (source, level, message) => logs.Enqueue((source, level, message)),
            banners.Enqueue);

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, 0, 99d));
        hub.PublishBar(BarFor(instrument, 1, 100d));
        hub.PublishBar(BarFor(instrument, 2, 101d));

        await WaitUntilAsync(() => visualizer?.ViewState is { IsReady: true, LastPrice: 101d });
        Assert.Empty(banners);

        hub.PublishQuote(QuoteFor(instrument, sequence: 1, bid: 109d, ask: 111d));
        await WaitUntilAsync(() => banners.Count == 1);

        Assert.NotNull(visualizer);
        Assert.True(visualizer.ViewState.IsReady);
        Assert.Equal(3, visualizer.ViewState.SampleCount);
        Assert.Equal(110d, visualizer.ViewState.LastPrice);
        Assert.True(visualizer.ViewState.IsOutsideBand);
        Assert.True(visualizer.ViewState.UpperBand < visualizer.ViewState.LastPrice);
        Assert.Single(logs);
        Assert.Single(banners);
        Assert.Equal("WARN", logs.Single().Level);
        Assert.Equal(AlertLevel.Warning, banners.Single().Level);
        Assert.Equal(nameof(SpreadBandVisualizer), banners.Single().Source);

        await runtime.StopAsync();
    }

    [Fact]
    public void SpreadBandVisualizerSaysWhyItIsEmptyBeforeItHasData()
    {
        // A blank panel is indistinguishable from a broken one, so an unready visualizer has to say
        // something. This is the state a user sees for the first few seconds of every session.
        var surface = new RecordingRenderSurface();

        new SpreadBandVisualizer().Draw(surface);

        Assert.Single(surface.Panels);
        Assert.Contains(surface.Texts, entry => entry.Text.Contains("Waiting", StringComparison.Ordinal));
        Assert.Empty(surface.Points);
    }

    [Fact]
    public async Task SpreadBandVisualizerDrawsTheBandItComputed()
    {
        // The check the samples did not have: that a visualizer which computes correctly also PAINTS.
        // Compiling and drawing nothing is the easiest mistake to ship and the hardest to notice.
        var instrument = new InstrumentId(7202);
        var hub = new InMemoryMarketDataHub();
        SpreadBandVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new SpreadBandVisualizer(),
            new Dictionary<string, object?>
            {
                [SpreadBandVisualizer.InstrumentParameter] = instrument,
                [SpreadBandVisualizer.LookbackParameter] = 3,
                [SpreadBandVisualizer.BandMultiplierParameter] = 2d,
            },
            hub,
            new TestClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, 0, 99d));
        hub.PublishBar(BarFor(instrument, 1, 100d));
        hub.PublishBar(BarFor(instrument, 2, 101d));
        await WaitUntilAsync(() => visualizer?.ViewState is { IsReady: true });

        var surface = new RecordingRenderSurface();
        visualizer!.Draw(surface);

        // The midpoint and the price it is measured against, on one scale.
        Assert.Contains("Midpoint", surface.SeriesNames);
        Assert.Contains("Price", surface.SeriesNames);
        Assert.NotEmpty(surface.Points);

        // The envelope is a FILLED REGION now, not two stroked lines — `Bands.Draw` rather than a
        // hand-rolled pair of series, which is why "Upper band" and "Lower band" are no longer series
        // names. The region is what makes the two edges read as one thing.
        Assert.NotEmpty(surface.Rectangles);

        // Colours come from theme roles, never literals, or the picture is unreadable in one theme.
        Assert.Contains(surface.Calls, call => call.Kind == "Theme");

        // And the statistics beside it. A line chart with no numbers is where most generated
        // visualizers stop; the exemplar has to show the habit it is teaching.
        Assert.Contains(surface.Texts, entry => entry.Text.Contains("In band", StringComparison.Ordinal));
        Assert.Contains(surface.Texts, entry => entry.Text.Contains("Band width", StringComparison.Ordinal));

        await runtime.StopAsync();
    }

    [Fact]
    public void SpreadBandVisualizerDeclaresTheTwoPanelWindowItDraws()
    {
        // The exemplar is what Hyperion is shown as the shape to aim for, so it has to demonstrate the
        // layout vocabulary rather than only the drawing one. Both panels paint: a declared layout
        // whose panels are blank is the same empty window with extra headers.
        var layout = new SpreadBandVisualizer().Layout;

        Assert.False(layout.IsSingle);
        Assert.Equal(["Band", "Statistics"], layout.Panels().Select(p => p.Title));

        foreach (var panel in layout.Panels())
        {
            var surface = new RecordingRenderSurface();
            panel.Draw(surface);

            // Before any data it says what it is waiting for rather than painting nothing — the state
            // a user sees for the first seconds of every session.
            Assert.False(surface.IsBlank, $"panel '{panel.Title}' painted nothing");
        }
    }

    [Fact]
    public async Task SpreadBandVisualizerMarksTheBreach()
    {
        // The band exists to show breaches, so the breach has to be visible in the picture and not
        // only in the alert.
        var instrument = new InstrumentId(7203);
        var hub = new InMemoryMarketDataHub();
        SpreadBandVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new SpreadBandVisualizer(),
            new Dictionary<string, object?>
            {
                [SpreadBandVisualizer.InstrumentParameter] = instrument,
                [SpreadBandVisualizer.LookbackParameter] = 3,
                [SpreadBandVisualizer.BandMultiplierParameter] = 2d,
            },
            hub,
            new TestClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, 0, 99d));
        hub.PublishBar(BarFor(instrument, 1, 100d));
        hub.PublishBar(BarFor(instrument, 2, 101d));
        await WaitUntilAsync(() => visualizer?.ViewState is { IsReady: true });
        hub.PublishQuote(QuoteFor(instrument, sequence: 1, bid: 109d, ask: 111d));
        await WaitUntilAsync(() => visualizer?.ViewState.IsOutsideBand == true);

        var surface = new RecordingRenderSurface();
        visualizer!.Draw(surface);

        Assert.NotEmpty(surface.Markers);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task MovingAverageCrossDrawsBothAveragesAndMarksTheCross()
    {
        // A strategy is not obliged to draw, but when it does the picture has to show the signal it
        // acted on — otherwise the chart and the book disagree about what happened.
        var instrument = new InstrumentId(7102);
        var kernel = new MovingAverageCrossKernel();
        var hub = new InMemoryMarketDataHub();
        var clock = new TestClock(Epoch);
        var book = new RecordingVirtualBook();
        var parameters = new SandboxParameters(
            kernel.Schema,
            new Dictionary<string, object?>
            {
                [MovingAverageCrossKernel.InstrumentParameter] = instrument,
                [MovingAverageCrossKernel.FastPeriodParameter] = 2,
                [MovingAverageCrossKernel.SlowPeriodParameter] = 3,
                [MovingAverageCrossKernel.UseProtectiveStopParameter] = true,
                [MovingAverageCrossKernel.ProtectiveStopPercentParameter] = 5d,
            });
        using var data = new ScopedMarketDataView(
            new HashSet<InstrumentId> { instrument },
            kernel.DataRequirement,
            hub,
            retentionBound: 16);
        var context = new TestStrategyRuntimeContext(
            data,
            clock,
            parameters,
            book,
            new MediatedAlertSink(nameof(MovingAverageCrossKernel), clock, (_, _, _) => { }, _ => { }));

        // Before any data: the panel must explain itself rather than sit blank.
        var empty = new RecordingRenderSurface();
        kernel.Draw(empty);
        Assert.Single(empty.Panels);
        Assert.Contains(empty.Texts, entry => entry.Text.Contains("Waiting", StringComparison.Ordinal));

        await kernel.OnStartAsync(context, CancellationToken.None);

        // The same series that produces a long then a flat target in the test above.
        var closes = new[] { 3d, 2d, 1d, 4d, 0d, 0d };
        for (var index = 0; index < closes.Length; index++)
        {
            var bar = BarFor(instrument, index, closes[index]);
            hub.PublishBar(bar);
            await kernel.OnBarAsync(bar, context, CancellationToken.None);
        }

        var surface = new RecordingRenderSurface();
        kernel.Draw(surface);

        Assert.Contains("Fast SMA", surface.SeriesNames);
        Assert.Contains("Slow SMA", surface.SeriesNames);
        Assert.NotEmpty(surface.Points);

        // Two book intents were submitted, so two crosses must be marked. The picture and the book
        // have to tell the same story.
        Assert.Equal(book.Intents.Count, surface.Markers.Count);
        Assert.Equal(2, surface.Markers.Count);

        // Shape carries the direction, not colour alone.
        Assert.Contains(surface.Markers, marker => marker.Shape == RenderMarkerShape.Triangle);
        Assert.Contains(surface.Markers, marker => marker.Shape == RenderMarkerShape.Diamond);
    }

    private static OhlcvBar BarFor(InstrumentId instrument, int sequence, double close) =>
        new(
            instrument,
            BarSize.OneMinute,
            Epoch.AddMinutes(sequence),
            close,
            close,
            close,
            close,
            sequence + 1,
            BrokerKind.Simulated,
            IsFinal: true);

    private static Quote QuoteFor(
        InstrumentId instrument,
        long sequence,
        double bid,
        double ask) =>
        new(
            instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            bid,
            ask,
            10,
            10,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            var delay = Task.Delay(TimeSpan.FromMilliseconds(10));
            if (await Task.WhenAny(delay, timeout) == timeout)
                break;
            await delay;
        }

        Assert.True(condition(), "The asynchronous sample condition did not complete within five seconds.");
    }
}
