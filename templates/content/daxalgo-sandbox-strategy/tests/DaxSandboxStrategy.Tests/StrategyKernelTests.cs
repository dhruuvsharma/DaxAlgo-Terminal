using DaxAlgo.Sdk;
using SandboxStrategy;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using Xunit;

namespace SandboxStrategy.Tests;

public sealed class StrategyKernelTests
{
    [Fact]
    public async Task Final_up_bar_submits_long_target_and_alerts()
    {
        var instrument = new InstrumentId(42);
        var earlier = Bar(instrument, minute: 0, close: 100d);
        var latest = Bar(instrument, minute: 1, close: 101d);
        var data = new FixedMarketData(instrument, [earlier, latest]);
        var parameters = new FixedParameters(
            new StrategyKernel().Schema,
            instrument,
            targetUnits: 2d);
        var book = new RecordingBook();
        var alerts = new RecordingAlerts();
        var context = new TestContext(data, parameters, book, alerts);
        var kernel = new StrategyKernel();

        await kernel.OnStartAsync(context, CancellationToken.None);
        await kernel.OnBarAsync(latest, context, CancellationToken.None);

        var intent = Assert.Single(book.Intents);
        Assert.Equal(instrument, intent.Instrument);
        Assert.Equal(2d, intent.TargetUnits);
        Assert.Single(alerts.Messages);
    }

    private static OhlcvBar Bar(InstrumentId instrument, int minute, double close) =>
        new(
            instrument,
            BarSize.OneMinute,
            new DateTime(2026, 1, 1, 9, minute, 0, DateTimeKind.Utc),
            close,
            close,
            close,
            close,
            100,
            BrokerKind.Simulated,
            IsFinal: true);

    private sealed class TestContext(
        IMarketDataView data,
        IParameters parameters,
        IVirtualBook book,
        IAlertSink alerts) : IStrategyRuntimeContext
    {
        public IMarketDataView Data { get; } = data;
        public IClock Clock { get; } = new FixedClock();
        public IParameters Parameters { get; } = parameters;
        public IVirtualBook Book { get; } = book;
        public IAlertSink Alerts { get; } = alerts;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 9, 1, 0, DateTimeKind.Utc);
    }

    private sealed class FixedMarketData(
        InstrumentId instrument,
        IReadOnlyList<OhlcvBar> bars) : IMarketDataView
    {
        public IReadOnlySet<InstrumentId> Instruments { get; } = new HashSet<InstrumentId> { instrument };
        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId requested, BarSize size, int maxCount) =>
            requested == instrument && size == BarSize.OneMinute
                ? bars.TakeLast(maxCount).ToArray()
                : [];

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId requested, int maxCount) => [];
        public DepthSnapshot? LatestDepth(InstrumentId requested) => null;
        public IReadOnlyList<TradePrint> RecentTrades(InstrumentId requested, int maxCount) => [];
    }

    private sealed class FixedParameters(
        StrategyParameterSchema schema,
        InstrumentId instrument,
        double targetUnits) : IParameters
    {
        public StrategyParameterSchema Schema { get; } = schema;
        public int GetInt(string name) => throw new NotSupportedException();
        public long GetLong(string name) => throw new NotSupportedException();
        public double GetDouble(string name) => name == StrategyKernel.TargetUnitsParameter
            ? targetUnits
            : throw new KeyNotFoundException(name);
        public bool GetBool(string name) => throw new NotSupportedException();
        public string GetString(string name) => throw new NotSupportedException();
        public string GetText(string name) => throw new NotSupportedException();
        public TEnum GetEnum<TEnum>(string name) where TEnum : struct, Enum =>
            throw new NotSupportedException();
        public InstrumentId GetInstrument(string name) => name == StrategyKernel.InstrumentParameter
            ? instrument
            : throw new KeyNotFoundException(name);
    }

    private sealed class RecordingBook : IVirtualBook
    {
        public List<VirtualTargetIntent> Intents { get; } = [];
        public void SubmitTarget(VirtualTargetIntent intent) => Intents.Add(intent);
    }

    private sealed class RecordingAlerts : IAlertSink
    {
        public List<string> Messages { get; } = [];
        public void Alert(string message, AlertLevel level, string? dedupeKey = null) => Messages.Add(message);
    }
}
