using System.Collections.Concurrent;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class SandboxVisualizerRuntimeTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task StartAutoRunsAndFeedsBarsWithoutAnExplicitRunGate()
    {
        var instrument = new InstrumentId(901);
        var schema = InstrumentSchema(instrument);
        var hub = new FakeMarketDataHub();
        RecordingVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new RecordingVisualizer(schema, StrategyDataRequirement.Bars),
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        Assert.Equal(SandboxVisualizerRuntimeState.Idle, runtime.State);

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, sequence: 1, close: 101));

        await WaitUntilAsync(() => visualizer?.BarCount == 1);
        Assert.Equal(SandboxVisualizerRuntimeState.Running, runtime.State);
        Assert.True(runtime.IsRunning);
        Assert.False(runtime.IsPaused);
        Assert.Equal(1, visualizer!.StartCount);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task VisualizerAlertsUseTheMediatedDedupeThrottleLogAndBannerRoutes()
    {
        var instrument = new InstrumentId(902);
        var schema = new StrategyParameterSchema(
            StrategyParameter.Instrument("instrument", "Instrument", instrument),
            StrategyParameter.Number("level", "Level", 100));
        var hub = new FakeMarketDataHub();
        var clock = new MutableClock(Epoch);
        var logs = new ConcurrentQueue<(string Source, string Level, string Message)>();
        var banners = new ConcurrentQueue<AlertRecord>();
        ThresholdAlertVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new ThresholdAlertVisualizer(schema),
            currentValues: null,
            hub,
            clock,
            (source, level, message) => logs.Enqueue((source, level, message)),
            banners.Enqueue);

        await runtime.StartAsync();

        hub.PublishBar(BarFor(instrument, sequence: 1, close: 101));
        hub.PublishBar(BarFor(instrument, sequence: 2, close: 101));
        await WaitUntilAsync(() => visualizer?.BarCount == 2);

        Assert.Single(logs);
        Assert.Single(banners);
        Assert.Equal("WARN", logs.Single().Level);
        Assert.Equal(AlertLevel.Warning, banners.Single().Level);

        clock.UtcNow += MediatedAlertSink.DefaultWindow + TimeSpan.FromTicks(1);
        for (var sequence = 3; sequence <= 23; sequence++)
            hub.PublishBar(BarFor(instrument, sequence, close: 200 + sequence));

        await WaitUntilAsync(() => visualizer?.BarCount == 23);

        Assert.Equal(21, logs.Count);
        Assert.Equal(21, banners.Count);
        Assert.All(logs, log => Assert.Equal(nameof(ThresholdAlertVisualizer), log.Source));
        Assert.All(banners, banner => Assert.Equal(nameof(ThresholdAlertVisualizer), banner.Source));

        await runtime.StopAsync();
    }

    [Fact]
    public async Task RapidEventsStaySerializedAndDropTheOldestQueuedWorkAtTheBound()
    {
        var instrument = new InstrumentId(903);
        var hub = new FakeMarketDataHub();
        var visualizer = new BlockingVisualizer(InstrumentSchema(instrument));
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { },
            retentionBound: 2);

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, sequence: 1, close: 1));
        await visualizer.FirstHandlerEntered.WaitAsync(TimeSpan.FromSeconds(5));

        for (var sequence = 2; sequence <= 10; sequence++)
            hub.PublishBar(BarFor(instrument, sequence, close: sequence));

        Assert.Equal(2, runtime.QueueCapacity);
        Assert.Equal(7, runtime.DroppedEventCount);

        visualizer.ReleaseFirstHandler();
        await WaitUntilAsync(() => visualizer.ProcessedCloses.Count == 3);

        Assert.Equal(new[] { 1, 9, 10 }, visualizer.ProcessedCloses.ToArray());
        Assert.Equal(1, visualizer.MaxConcurrency);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task PausePreservesThenResumeRebuildsAndStopDisposesEverySubscription()
    {
        var instrument = new InstrumentId(904);
        var replacementInstrument = new InstrumentId(914);
        var schema = new StrategyParameterSchema(
            StrategyParameter.Instrument("instrument", "Instrument", instrument),
            StrategyParameter.Number("level", "Level", 10));
        var hub = new FakeMarketDataHub();
        var instances = new List<RecordingVisualizer>();
        await using var runtime = new SandboxVisualizerRuntime(
            () =>
            {
                var instance = new RecordingVisualizer(schema, StrategyDataRequirement.Bars);
                instances.Add(instance);
                return instance;
            },
            new Dictionary<string, object?> { ["level"] = 12d },
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        var first = Assert.Single(instances);
        var activeWhileRunning = hub.ActiveSubscriptionCount;
        Assert.Equal(12d, first.ObservedLevel);

        hub.PublishBar(BarFor(instrument, sequence: 1, close: 11));
        await WaitUntilAsync(() => first.BarCount == 1);

        runtime.Pause();
        Assert.Equal(SandboxVisualizerRuntimeState.Paused, runtime.State);
        Assert.True(runtime.IsRunning);
        Assert.True(runtime.IsPaused);
        Assert.Equal(Enum.GetValues<BarSize>().Length, hub.ActiveSubscriptionCount);
        Assert.Equal(0, first.StopCount);
        Assert.Equal(0, first.DisposeCount);

        hub.PublishBar(BarFor(instrument, sequence: 2, close: 12));
        Assert.Equal(1, first.BarCount);

        runtime.SetParameter("level", 25d);
        runtime.SetParameter("instrument", replacementInstrument);
        runtime.Resume();

        Assert.Equal(2, instances.Count);
        var second = instances[1];
        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.StartCount);
        Assert.Equal(25d, second.ObservedLevel);
        Assert.NotSame(first.StartContext, second.StartContext);
        Assert.Equal(SandboxVisualizerRuntimeState.Running, runtime.State);
        Assert.Equal(activeWhileRunning, hub.ActiveSubscriptionCount);

        hub.PublishBar(BarFor(instrument, sequence: 3, close: 30));
        hub.PublishBar(BarFor(replacementInstrument, sequence: 4, close: 31));
        await WaitUntilAsync(() => second.BarCount == 1);
        Assert.Equal(1, first.BarCount);
        Assert.Equal(replacementInstrument, Assert.Single(second.BarInstruments));

        await runtime.StopAsync();
        await runtime.StopAsync();

        Assert.Equal(SandboxVisualizerRuntimeState.Stopped, runtime.State);
        Assert.False(runtime.IsRunning);
        Assert.False(runtime.IsPaused);
        Assert.Equal(1, second.StopCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
        Assert.Equal(hub.TotalSubscriptionCount, hub.TotalDisposalCount);
    }

    [Fact]
    public async Task StopFromPausedDisposesThePreservedVisualizerAndContext()
    {
        var instrument = new InstrumentId(907);
        var hub = new FakeMarketDataHub();
        var visualizer = new RecordingVisualizer(InstrumentSchema(instrument), StrategyDataRequirement.Bars);
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        await runtime.PauseAsync();

        Assert.Equal(0, visualizer.StopCount);
        Assert.Equal(0, visualizer.DisposeCount);
        Assert.Equal(Enum.GetValues<BarSize>().Length, hub.ActiveSubscriptionCount);

        await runtime.StopAsync();

        Assert.Equal(1, visualizer.StopCount);
        Assert.Equal(1, visualizer.DisposeCount);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
        Assert.Equal(hub.TotalSubscriptionCount, hub.TotalDisposalCount);
    }

    [Fact]
    public async Task OnlyDeclaredInstrumentsAndAuthorizedStreamsReachTheVisualizer()
    {
        var primary = new InstrumentId(905);
        var secondary = new InstrumentId(906);
        var outside = new InstrumentId(999);
        var schema = new StrategyParameterSchema(
            StrategyParameter.Instrument("primary", "Primary", primary),
            StrategyParameter.Instrument("secondary", "Secondary", secondary));
        var hub = new FakeMarketDataHub();
        RecordingVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new RecordingVisualizer(schema, StrategyDataRequirement.Bars),
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();

        var barSizes = Enum.GetValues<BarSize>().Length;
        Assert.Equal(2 * barSizes * 2, hub.BarSubscriptionCount);
        Assert.Equal(0, hub.QuoteSubscriptionCount);
        Assert.Equal(0, hub.TradeSubscriptionCount);
        Assert.Equal(0, hub.DepthSubscriptionCount);

        hub.PublishBar(BarFor(primary, sequence: 1, close: 10));
        hub.PublishBar(BarFor(secondary, sequence: 2, close: 20));
        hub.PublishBar(BarFor(outside, sequence: 3, close: 30));
        hub.PublishQuote(QuoteFor(primary, sequence: 4));
        hub.PublishTrade(TradeFor(primary, sequence: 5));
        hub.PublishDepth(primary, DepthFor(sequence: 6));

        await WaitUntilAsync(() => visualizer?.BarCount == 2);

        Assert.Equal(new[] { primary, secondary }, visualizer!.BarInstruments.ToArray());
        Assert.Equal(0, visualizer.QuoteCount);
        Assert.Equal(0, visualizer.TradeCount);
        Assert.Equal(0, visualizer.DepthCount);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task EveryAuthorizedStreamKindReachesTheVisualizer()
    {
        var instrument = new InstrumentId(908);
        var hub = new FakeMarketDataHub();
        RecordingVisualizer? visualizer = null;
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer = new RecordingVisualizer(
                InstrumentSchema(instrument),
                StrategyDataRequirement.L1 |
                StrategyDataRequirement.Bars |
                StrategyDataRequirement.Depth |
                StrategyDataRequirement.TradeTape),
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();

        hub.PublishQuote(QuoteFor(instrument, sequence: 1));
        hub.PublishTrade(TradeFor(instrument, sequence: 2));
        hub.PublishDepth(instrument, DepthFor(sequence: 3));
        hub.PublishBar(BarFor(instrument, sequence: 4, close: 40));

        await WaitUntilAsync(() =>
            visualizer?.QuoteCount == 1 &&
            visualizer.TradeCount == 1 &&
            visualizer.DepthCount == 1 &&
            visualizer.BarCount == 1);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task PartialPumpSubscriptionFailureDisposesAllEarlierSubscriptionsAndOwnedState()
    {
        var instrument = new InstrumentId(909);
        var barSizes = Enum.GetValues<BarSize>().Length;
        var failOnAttempt = barSizes + 2;
        var hub = new FailingSubscriptionHub(failOnAttempt);
        var visualizer = new RecordingVisualizer(InstrumentSchema(instrument), StrategyDataRequirement.Bars);
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());

        Assert.Equal(SandboxVisualizerRuntimeState.Idle, runtime.State);
        Assert.Equal(failOnAttempt, hub.SubscriptionAttemptCount);
        Assert.Equal(failOnAttempt - 1, hub.DisposalCount);
        Assert.Equal(1, visualizer.StartCount);
        Assert.Equal(1, visualizer.StopCount);
        Assert.Equal(1, visualizer.DisposeCount);
    }

    [Fact]
    public async Task LifecycleReentryFromAVisualizerCallbackFailsFastInsteadOfDeadlocking()
    {
        var instrument = new InstrumentId(910);
        var hub = new FakeMarketDataHub();
        var visualizer = new ReentrantLifecycleVisualizer(InstrumentSchema(instrument));
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });
        visualizer.LifecycleCall = () => runtime.StopAsync();

        await runtime.StartAsync();
        hub.PublishBar(BarFor(instrument, sequence: 1, close: 10));
        await visualizer.AttemptCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        var failure = Assert.IsType<InvalidOperationException>(visualizer.Failure);
        Assert.Contains("callback", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SandboxVisualizerRuntimeState.Running, runtime.State);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task FactoryLifecycleReentryFailsFastInsteadOfWaitingOnItsOwnStartGate()
    {
        var instrument = new InstrumentId(912);
        var hub = new FakeMarketDataHub();
        var visualizer = new RecordingVisualizer(InstrumentSchema(instrument), StrategyDataRequirement.Bars);
        SandboxVisualizerRuntime? runtime = null;
        runtime = new SandboxVisualizerRuntime(
            () =>
            {
                _ = runtime!.StopAsync();
                return visualizer;
            },
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });
        await using var ownedRuntime = runtime;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());

        Assert.Contains("callback", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SandboxVisualizerRuntimeState.Idle, runtime.State);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task FireAndForgetCallbackWorkDoesNotRemainFalselyReentrantAfterCallbackReturns()
    {
        var instrument = new InstrumentId(913);
        var hub = new FakeMarketDataHub();
        var visualizer = new DeferredLifecycleVisualizer(InstrumentSchema(instrument));
        await using var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });
        visualizer.LifecycleCall = () => runtime.StopAsync();

        await runtime.StartAsync();
        visualizer.ReleaseDeferredCall();
        await visualizer.DeferredCallCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(visualizer.Failure);
        Assert.Equal(SandboxVisualizerRuntimeState.Stopped, runtime.State);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task ConcurrentDisposeCallersAwaitTheSameCompleteTeardown()
    {
        var instrument = new InstrumentId(911);
        var hub = new FakeMarketDataHub();
        var visualizer = new BlockingStopVisualizer(InstrumentSchema(instrument));
        var runtime = new SandboxVisualizerRuntime(
            () => visualizer,
            currentValues: null,
            hub,
            new MutableClock(Epoch),
            (_, _, _) => { },
            _ => { });

        await runtime.StartAsync();
        var firstDispose = runtime.DisposeAsync().AsTask();
        Task? secondDispose = null;
        var secondCompletedEarly = true;
        try
        {
            await visualizer.StopEntered.WaitAsync(TimeSpan.FromSeconds(5));
            secondDispose = runtime.DisposeAsync().AsTask();
            secondCompletedEarly = secondDispose.IsCompleted;
        }
        finally
        {
            visualizer.ReleaseStop();
            await firstDispose.WaitAsync(TimeSpan.FromSeconds(5));
            if (secondDispose is not null)
                await secondDispose.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(secondCompletedEarly);
        Assert.Equal(1, visualizer.DisposeCount);
        Assert.Equal(0, hub.ActiveSubscriptionCount);
        Assert.Equal(hub.TotalSubscriptionCount, hub.TotalDisposalCount);
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

    private static Quote QuoteFor(InstrumentId instrument, long sequence) =>
        new(
            instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            sequence,
            sequence + 1,
            10,
            11,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static TradePrint TradeFor(InstrumentId instrument, long sequence) =>
        new(
            instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            sequence,
            10,
            AggressorSide.Buy,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static DepthSnapshot DepthFor(int sequence) =>
        new(
            Epoch.AddSeconds(sequence),
            new[] { new DepthLevel(sequence, sequence) },
            new[] { new DepthLevel(sequence + 1, sequence) });

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

        Assert.True(condition(), "The asynchronous visualizer condition did not complete within five seconds.");
    }

    private sealed class RecordingVisualizer(
        StrategyParameterSchema schema,
        StrategyDataRequirement dataRequirement) : IVisualizer, IDisposable
    {
        private int _startCount;
        private int _stopCount;
        private int _disposeCount;
        private int _quoteCount;
        private int _tradeCount;
        private int _depthCount;
        private int _barCount;
        private double _observedLevel;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement { get; } = dataRequirement;

        public int StartCount => Volatile.Read(ref _startCount);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int QuoteCount => Volatile.Read(ref _quoteCount);

        public int TradeCount => Volatile.Read(ref _tradeCount);

        public int DepthCount => Volatile.Read(ref _depthCount);

        public int BarCount => Volatile.Read(ref _barCount);

        public double ObservedLevel => Volatile.Read(ref _observedLevel);

        public ConcurrentQueue<InstrumentId> BarInstruments { get; } = new();

        public IVisualizerContext? StartContext { get; private set; }

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
        {
            StartContext = context;
            if (Schema.Find("level") is not null)
                Volatile.Write(ref _observedLevel, context.Parameters.GetDouble("level"));
            Interlocked.Increment(ref _startCount);
            return Task.CompletedTask;
        }

        public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _quoteCount);
            return Task.CompletedTask;
        }

        public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _tradeCount);
            return Task.CompletedTask;
        }

        public Task OnDepthAsync(
            InstrumentId instrument,
            DepthSnapshot depth,
            IVisualizerContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _depthCount);
            return Task.CompletedTask;
        }

        public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            BarInstruments.Enqueue(bar.InstrumentId);
            Interlocked.Increment(ref _barCount);
            return Task.CompletedTask;
        }

        public Task OnStopAsync(IVisualizerContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class ThresholdAlertVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        private double _level;
        private int _barCount;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public int BarCount => Volatile.Read(ref _barCount);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
        {
            _level = context.Parameters.GetDouble("level");
            return Task.CompletedTask;
        }

        public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            if (bar.Close >= _level)
            {
                context.Alerts.Alert(
                    $"Close {bar.Close} crossed level {_level}.",
                    AlertLevel.Warning,
                    $"close:{bar.Close}");
            }

            Interlocked.Increment(ref _barCount);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        private readonly TaskCompletionSource<bool> _firstHandlerEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstHandler =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrency;
        private int _maxConcurrency;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task FirstHandlerEntered => _firstHandlerEntered.Task;

        public ConcurrentQueue<int> ProcessedCloses { get; } = new();

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public async Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(concurrency);
            try
            {
                if (bar.Close == 1)
                {
                    _firstHandlerEntered.TrySetResult(true);
                    await _releaseFirstHandler.Task.WaitAsync(ct);
                }

                ProcessedCloses.Enqueue((int)bar.Close);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public void ReleaseFirstHandler() => _releaseFirstHandler.TrySetResult(true);

        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref _maxConcurrency);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxConcurrency, candidate, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed class ReentrantLifecycleVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        private readonly TaskCompletionSource<bool> _attemptCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Func<Task>? LifecycleCall { get; set; }

        public Task AttemptCompleted => _attemptCompleted.Task;

        public Exception? Failure => Volatile.Read(ref _failure);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            try
            {
                _ = (LifecycleCall
                     ?? throw new InvalidOperationException("The lifecycle callback is unavailable."))();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ex);
            }
            finally
            {
                _attemptCompleted.TrySetResult(true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class BlockingStopVisualizer(StrategyParameterSchema schema) : IVisualizer, IDisposable
    {
        private readonly TaskCompletionSource<bool> _stopEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseStop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task StopEntered => _stopEntered.Task;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public async Task OnStopAsync(IVisualizerContext context, CancellationToken ct)
        {
            _stopEntered.TrySetResult(true);
            await _releaseStop.Task.WaitAsync(ct);
        }

        public void ReleaseStop() => _releaseStop.TrySetResult(true);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class DeferredLifecycleVisualizer(StrategyParameterSchema schema) : IVisualizer
    {
        private readonly TaskCompletionSource<bool> _releaseDeferredCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _deferredCallCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;

        public StrategyParameterSchema Schema { get; } = schema;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Func<Task>? LifecycleCall { get; set; }

        public Task DeferredCallCompleted => _deferredCallCompleted.Task;

        public Exception? Failure => Volatile.Read(ref _failure);

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                await _releaseDeferredCall.Task;
                try
                {
                    await (LifecycleCall
                           ?? throw new InvalidOperationException("The lifecycle callback is unavailable."))();
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _failure, ex);
                }
                finally
                {
                    _deferredCallCompleted.TrySetResult(true);
                }
            });

            return Task.CompletedTask;
        }

        public void ReleaseDeferredCall() => _releaseDeferredCall.TrySetResult(true);
    }
}
