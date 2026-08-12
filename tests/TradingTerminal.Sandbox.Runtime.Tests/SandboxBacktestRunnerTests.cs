using System.Text.Json;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Sandbox.Runtime.Tests;

public sealed class SandboxBacktestRunnerTests
{
    private static readonly InstrumentId Instrument = new(42);
    private static readonly DateTime Epoch = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FixedBarsProduceHandComputedCurveAndCompletedSnapshot()
    {
        var bars = Bars(95d, 97d, 100d, 104d, 108d, 110d);
        var runner = CreateRunner(
            () => new ThirdBarLongSixthBarFlatKernel(),
            new Dictionary<string, object?>
            {
                ["entryBar"] = 3,
                ["exitBar"] = 6,
            });

        var result = await runner.RunAsync(bars);

        Assert.Equal(new[] { 0d, 0d, 1d, 1d, 1d, 0d },
            result.EquityCurve.Select(static point => point.Snapshot.PositionUnits));
        var expectedEquity = new[]
        {
            100_000d,
            100_000d,
            99_999.8d,
            100_039.8d,
            100_079.8d,
            100_099.58d,
        };
        for (var index = 0; index < expectedEquity.Length; index++)
            Assert.Equal(expectedEquity[index], result.EquityCurve[index].Equity, precision: 10);

        var final = result.FinalSnapshot;
        Assert.Equal(Instrument, final.Instrument);
        Assert.Equal(0d, final.PositionUnits);
        Assert.Equal(0d, final.PositionQuantity);
        Assert.Equal(0d, final.AverageEntryPrice);
        Assert.Equal(0L, final.BarsHeld);
        Assert.Equal(100_099.58d, final.Equity, precision: 10);
        Assert.Equal(100d, final.RealizedGrossProfitLoss, precision: 10);
        Assert.Equal(0.21d, final.CommissionTotal, precision: 10);
        Assert.Equal(0.21d, final.SlippageTotal, precision: 10);
        Assert.Equal(100_099.8d, final.EquityPeak, precision: 10);
        Assert.Equal(0.22d, final.MaximumDrawdown, precision: 10);
        Assert.Equal(1L, final.LifetimeClosedTripCount);
        Assert.Equal(1L, final.LifetimeWinningTripCount);
        Assert.Equal(0L, final.LifetimeLosingTripCount);
        Assert.Equal(1L, final.RetainedTradeCount);
        Assert.Equal(1L, final.Streak);
        Assert.True(final.IsComplete);

        var alert = Assert.Single(result.Alerts);
        Assert.Equal(bars[2].OpenTimeUtc, alert.TimestampUtc);
        Assert.Equal("Entered the model position.", alert.Message);
    }

    [Fact]
    public async Task IdenticalInputsProduceByteIdenticalResults()
    {
        var bars = Bars(95d, 97d, 100d, 104d, 108d, 110d);
        var runner = CreateRunner(
            () => new ThirdBarLongSixthBarFlatKernel(),
            new Dictionary<string, object?>
            {
                ["entryBar"] = 3,
                ["exitBar"] = 6,
            });

        var first = await runner.RunAsync(bars);
        var second = await runner.RunAsync(bars.AsEnumerable());

        Assert.Equal(first, second);
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(first),
            JsonSerializer.SerializeToUtf8Bytes(second));
    }

    [Fact]
    public async Task ProtectiveStopExitsAtTheTriggeringBarClose()
    {
        var bars = Bars(90d, 100d, 105d, 95d);
        var runner = CreateRunner(() => new StopKernel());

        var result = await runner.RunAsync(bars);

        var triggerPoint = result.EquityCurve[3].Snapshot;
        Assert.Equal(0d, triggerPoint.PositionUnits);
        Assert.Equal(-50d, triggerPoint.RealizedGrossProfitLoss, precision: 10);
        Assert.Equal(0.195d, triggerPoint.CommissionTotal, precision: 10);
        Assert.Equal(0.195d, triggerPoint.SlippageTotal, precision: 10);
        Assert.Equal(99_949.61d, triggerPoint.Equity, precision: 10);
        Assert.Equal(1L, triggerPoint.LifetimeClosedTripCount);
        Assert.Equal(1L, triggerPoint.LifetimeLosingTripCount);
        Assert.Equal(-1L, triggerPoint.Streak);
        Assert.Equal(triggerPoint, result.FinalSnapshot with { IsComplete = false });
        Assert.True(result.FinalSnapshot.IsComplete);
    }

    [Fact]
    public async Task RecentBarsIncludesTheCurrentBarAndRetainsOnlyTheBoundedHistory()
    {
        var bars = Bars(100d, 101d, 102d, 103d);
        var kernel = new RecentBarsKernel();
        var runner = new SandboxBacktestRunner(
            () => kernel,
            parameterValues: null,
            Instrument,
            BarSize.OneMinute,
            retentionBound: 3);

        await runner.RunAsync(bars.AsEnumerable().Reverse());

        Assert.Equal(
            bars.Select(static bar => bar.OpenTimeUtc),
            kernel.Observed.Select(static observation => observation.Current.OpenTimeUtc));
        Assert.Equal(new[] { 1, 2, 3, 3 },
            kernel.Observed.Select(static observation => observation.History.Count));
        Assert.All(kernel.Observed, observation =>
        {
            Assert.Equal(observation.Current.OpenTimeUtc, observation.History[^1].OpenTimeUtc);
            Assert.All(
                observation.History,
                historical => Assert.True(historical.OpenTimeUtc <= observation.Current.OpenTimeUtc));
        });
    }

    [Fact]
    public async Task StartTargetUsesTheSameDeferredBookSemanticsAsLiveAndLifecycleRunsOnce()
    {
        var kernel = new StartTargetKernel();
        var runner = CreateRunner(() => kernel);

        var result = await runner.RunAsync(Bars(100d, 110d));

        Assert.Equal(1d, result.EquityCurve[0].Snapshot.PositionUnits);
        Assert.Equal(1, kernel.StartCount);
        Assert.Equal(1, kernel.StopCount);
        Assert.True(kernel.Disposed);
    }

    [Fact]
    public async Task AccountFaultRollsBackTheBarAlertsAndContinues()
    {
        var runner = CreateRunner(() => new FaultThenRecoverKernel());

        var result = await runner.RunAsync(Bars(100d, 101d));

        Assert.Equal(0d, result.EquityCurve[0].Snapshot.PositionUnits);
        Assert.Equal(1d, result.EquityCurve[1].Snapshot.PositionUnits);
        var alert = Assert.Single(result.Alerts);
        Assert.Equal(AlertLevel.Error, alert.Level);
        Assert.Contains("window was rolled back", alert.Message, StringComparison.Ordinal);
    }

    private static SandboxBacktestRunner CreateRunner(
        Func<IStrategyKernel> kernelFactory,
        IReadOnlyDictionary<string, object?>? parameterValues = null) =>
        new(
            kernelFactory,
            parameterValues,
            Instrument,
            BarSize.OneMinute,
            new ModelPortfolioAccountConfig(MaxAbsoluteUnits: 10, RetainedClosedTrips: 8),
            retentionBound: 16);

    private static OhlcvBar[] Bars(params double[] closes) =>
        closes.Select((close, index) => new OhlcvBar(
                Instrument,
                BarSize.OneMinute,
                Epoch.AddMinutes(index),
                close,
                close,
                close,
                close,
                100,
                BrokerKind.Simulated,
                IsFinal: true))
            .ToArray();

    private sealed class ThirdBarLongSixthBarFlatKernel : IStrategyKernel
    {
        private int _barCount;

        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Int("entryBar", "Entry bar", 3, min: 1),
            StrategyParameter.Int("exitBar", "Exit bar", 6, min: 2));

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            _barCount++;
            if (_barCount == context.Parameters.GetInt("entryBar"))
            {
                context.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: 90d);
                context.Alerts.Alert(
                    "Entered the model position.",
                    AlertLevel.Information,
                    "entry");
            }
            else if (_barCount == context.Parameters.GetInt("exitBar"))
            {
                context.Book.SetTargetPosition(Instrument, 0d);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StopKernel : IStrategyKernel
    {
        private int _barCount;

        public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            if (++_barCount == 2)
                context.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: 95d);

            return Task.CompletedTask;
        }
    }

    private sealed class RecentBarsKernel : IStrategyKernel
    {
        public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public List<RecentBarsObservation> Observed { get; } = new();

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Observed.Add(new RecentBarsObservation(
                bar,
                context.Data.RecentBars(Instrument, BarSize.OneMinute, int.MaxValue)));
            return Task.CompletedTask;
        }
    }

    private sealed class StartTargetKernel : IStrategyKernel, IDisposable
    {
        public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            StartCount++;
            context.Book.SetTargetPosition(Instrument, 1d);
            return Task.CompletedTask;
        }

        public Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FaultThenRecoverKernel : IStrategyKernel
    {
        private int _barCount;

        public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            if (++_barCount == 1)
                context.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: bar.Close);
            else
                context.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: 90d);

            return Task.CompletedTask;
        }
    }

    private sealed record RecentBarsObservation(
        OhlcvBar Current,
        IReadOnlyList<OhlcvBar> History);
}
