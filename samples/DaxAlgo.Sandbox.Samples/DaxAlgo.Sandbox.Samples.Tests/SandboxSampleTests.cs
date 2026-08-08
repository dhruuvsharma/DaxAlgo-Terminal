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
