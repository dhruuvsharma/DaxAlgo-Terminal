using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The smallest thing that will start a unit and feed it bars, so the ladder can be pointed at a real
/// one.
///
/// <para>Deliberately hand-built rather than borrowed from the sandbox runtime. The ladder's whole value
/// is that its verdicts can be trusted, and a harness simple enough to read in one sitting is a harness
/// that cannot quietly explain away a result. The full runtime belongs to rung 6, where driving a unit
/// the way the host does is the point rather than a means.</para>
/// </summary>
internal static class SampleDrive
{
    internal static readonly InstrumentId Instrument = new(9001);

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static RecordingParameters Run(
        StrategyParameterSchema schema,
        Func<IStrategyRuntimeContext, CancellationToken, Task> start,
        Func<OhlcvBar, IStrategyRuntimeContext, CancellationToken, Task> onBar,
        double[] closes,
        IReadOnlyDictionary<string, object?>? overrides = null)
    {
        var (parameters, data) = Prepare(schema, closes, overrides);
        var context = new Context(data, parameters);

        start(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Bars)
        {
            data.Advance(bar);
            onBar(bar, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        return parameters;
    }

    internal static RecordingParameters RunVisualizer(
        StrategyParameterSchema schema,
        Func<IVisualizerContext, CancellationToken, Task> start,
        Func<OhlcvBar, IVisualizerContext, CancellationToken, Task> onBar,
        double[] closes,
        IReadOnlyDictionary<string, object?>? overrides = null)
    {
        var (parameters, data) = Prepare(schema, closes, overrides);
        var context = new Context(data, parameters);

        start(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Bars)
        {
            data.Advance(bar);
            onBar(bar, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        return parameters;
    }

    /// <summary>
    /// Schema defaults, with the instrument pointed at the synthetic feed and anything the caller
    /// overrode applied on top.
    ///
    /// <para>Overrides matter more than they look. The sample strategy defaults to a 30-bar slow average,
    /// so on any series short enough to be readable in a test it returns before it ever reaches its
    /// trading path — and a drive that never reaches the code cannot be evidence about the code.</para>
    /// </summary>
    private static (RecordingParameters Parameters, Data Data) Prepare(
        StrategyParameterSchema schema,
        double[] closes,
        IReadOnlyDictionary<string, object?>? overrides)
    {
        var values = schema.Parameters.ToDictionary(
            p => p.Key,
            object? (p) => p.Kind == ParameterKind.Instrument ? Instrument : p.Default);

        foreach (var (key, value) in overrides ?? new Dictionary<string, object?>())
            values[key] = value;

        return (new RecordingParameters(new SandboxParameters(schema, values)), new Data(closes));
    }

    /// <summary>Bars revealed one at a time, so a unit asking for "recent" history sees only what had
    /// actually arrived — feeding it the whole series up front would hide every warm-up bug.</summary>
    private sealed class Data(double[] closes) : IMarketDataView
    {
        private readonly List<OhlcvBar> _seen = [];

        internal IReadOnlyList<OhlcvBar> Bars { get; } =
            [.. closes.Select((close, index) => new OhlcvBar(
                Instrument,
                BarSize.OneMinute,
                Epoch.AddMinutes(index),
                close, close, close, close,
                index + 1,
                BrokerKind.Simulated,
                IsFinal: true))];

        internal void Advance(OhlcvBar bar) => _seen.Add(bar);

        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.Bars | StrategyDataRequirement.L1;

        public IReadOnlySet<InstrumentId> Instruments { get; } = new HashSet<InstrumentId> { Instrument };

        public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount) =>
            instrument == Instrument && size == BarSize.OneMinute
                ? [.. _seen.TakeLast(maxCount)]
                : [];

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) =>
            _seen.Count == 0
                ? []
                : [new Quote(
                    Instrument,
                    Epoch.AddMinutes(_seen.Count),
                    Epoch.AddMinutes(_seen.Count),
                    _seen[^1].Close - 0.5d,
                    _seen[^1].Close + 0.5d,
                    10,
                    10,
                    BrokerKind.Simulated,
                    _seen.Count,
                    EventTimeApproximate: false)];

        public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount) => [];

        public DepthSnapshot? LatestDepth(InstrumentId instrument) => null;
    }

    private sealed class Context(IMarketDataView data, IParameters parameters)
        : IStrategyRuntimeContext, IVisualizerContext
    {
        public IMarketDataView Data { get; } = data;

        public IClock Clock { get; } = new FixedClock();

        public IParameters Parameters { get; } = parameters;

        public IVirtualBook Book { get; } = new DiscardingBook();

        public IAlertSink Alerts { get; } = new DiscardingAlerts();
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Epoch;
    }

    private sealed class DiscardingBook : IVirtualBook
    {
        public void SubmitTarget(VirtualTargetIntent intent)
        {
        }
    }

    private sealed class DiscardingAlerts : IAlertSink
    {
        public void Alert(string message, AlertLevel level, string? dedupeKey = null)
        {
        }

        public void AlertIf(bool condition, string message, AlertLevel level, string? dedupeKey = null)
        {
        }
    }
}
