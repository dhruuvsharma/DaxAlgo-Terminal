using DaxAlgo.Sdk;
using SandboxVisualizer;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using Xunit;

namespace SandboxVisualizer.Tests;

public sealed class BandVisualizerTests
{
    [Fact]
    public async Task Outside_close_updates_view_state_and_alerts()
    {
        var instrument = new InstrumentId(42);
        var bars = new[]
        {
            Bar(instrument, minute: 0, close: 100d),
            Bar(instrument, minute: 1, close: 100d),
            Bar(instrument, minute: 2, close: 110d),
        };
        var visualizer = new BandVisualizer();
        var alerts = new RecordingAlerts();
        var context = new TestContext(
            new FixedMarketData(instrument, bars),
            new FixedParameters(visualizer.Schema, instrument, lookback: 3, bandPercent: 1d),
            alerts);

        await visualizer.OnStartAsync(context, CancellationToken.None);
        await visualizer.OnBarAsync(bars[^1], context, CancellationToken.None);

        var state = Assert.IsType<BandViewState>(visualizer.ViewState);
        Assert.True(state.IsOutside);
        Assert.Equal(110d, state.LastPrice);
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
        IAlertSink alerts) : IVisualizerContext
    {
        public IMarketDataView Data { get; } = data;
        public IClock Clock { get; } = new FixedClock();
        public IParameters Parameters { get; } = parameters;
        public IAlertSink Alerts { get; } = alerts;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 1, 1, 9, 2, 0, DateTimeKind.Utc);
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
        int lookback,
        double bandPercent) : IParameters
    {
        public StrategyParameterSchema Schema { get; } = schema;
        public int GetInt(string name) => name == BandVisualizer.LookbackParameter
            ? lookback
            : throw new KeyNotFoundException(name);
        public long GetLong(string name) => throw new NotSupportedException();
        public double GetDouble(string name) => name == BandVisualizer.BandPercentParameter
            ? bandPercent
            : throw new KeyNotFoundException(name);
        public bool GetBool(string name) => throw new NotSupportedException();
        public string GetString(string name) => throw new NotSupportedException();
        public string GetText(string name) => throw new NotSupportedException();
        public TEnum GetEnum<TEnum>(string name) where TEnum : struct, Enum =>
            throw new NotSupportedException();
        public InstrumentId GetInstrument(string name) => name == BandVisualizer.InstrumentParameter
            ? instrument
            : throw new KeyNotFoundException(name);
    }

    private sealed class RecordingAlerts : IAlertSink
    {
        public List<string> Messages { get; } = [];
        public void Alert(string message, AlertLevel level, string? dedupeKey = null) => Messages.Add(message);
    }
}
