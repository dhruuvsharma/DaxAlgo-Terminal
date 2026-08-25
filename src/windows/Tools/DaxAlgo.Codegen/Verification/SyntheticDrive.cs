using DaxAlgo.Sdk;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Starts an authored unit and feeds it a short, deliberately awkward series so the rungs above have
/// something real to judge.
///
/// <para>Replaces a smoke test that drove forty-eight fabricated ticks past a stub clock and a stub
/// router. The stubs were the problem: a strategy could not fail against them in any way that mattered,
/// because the only thing it could do was place an order into nothing.</para>
///
/// <para>The series is chosen to be hostile in the ways market data is hostile — it falls, turns, runs,
/// and holds a flat stretch — because a flat stretch is a zero-width range and the commonest source of
/// a NaN in a first draft.</para>
/// </summary>
public static class SyntheticDrive
{
    /// <summary>The instrument every drive uses.</summary>
    public static readonly InstrumentId Instrument = new(1);

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Down, flat, then up — one hundred and twenty bars.
    ///
    /// <para>The length is not arbitrary. A strategy takes its <b>declared defaults</b> here, and a
    /// thirty-bar slow average is an ordinary default, so a short series would leave every such unit
    /// still warming up when the drive ended. It would then read none of its risk parameters, take no
    /// position, and draw nothing — and the ladder would report three failures for a strategy that was
    /// never given enough data to start. A drive that cannot reach the code cannot be evidence about
    /// the code.</para>
    ///
    /// <para>The shape matters too: a sustained fall then a sustained rise guarantees a crossing for any
    /// ordinary pair of periods, and the flat stretch in the middle is a zero-width range — the
    /// commonest source of a NaN in a first draft.</para>
    /// </summary>
    public static readonly double[] DefaultCloses = BuildDefaultSeries();

    private static double[] BuildDefaultSeries()
    {
        var closes = new List<double>(120);
        for (var i = 0; i < 45; i++) closes.Add(130d - i * 0.7d);      // falling
        for (var i = 0; i < 15; i++) closes.Add(closes[^1]);            // flat: a zero-width range
        for (var i = 0; i < 60; i++) closes.Add(closes[^1] + 0.9d);     // rising
        return [.. closes];
    }

    /// <summary>What one drive produced.</summary>
    public sealed record Result(
        RecordingParameters Parameters,
        RecordingVirtualBook Book,
        IReadOnlySet<InstrumentId> Instruments,
        bool Completed);

    /// <summary>Drives a strategy kernel through its full lifecycle.</summary>
    public static Result Run(IStrategyKernel kernel, double[]? closes = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var (context, data) = Build(kernel.Schema, closes);
        kernel.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Series)
        {
            data.Reveal(bar);
            kernel.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var quote in data.QuotesFor(bar))
                kernel.OnQuoteAsync(quote, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        kernel.OnStopAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return Finish(context, data);
    }

    /// <summary>Drives a visualizer through its full lifecycle.</summary>
    public static Result Run(IVisualizer visualizer, double[]? closes = null)
    {
        ArgumentNullException.ThrowIfNull(visualizer);

        var (context, data) = Build(visualizer.Schema, closes);
        visualizer.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Series)
        {
            data.Reveal(bar);
            visualizer.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var quote in data.QuotesFor(bar))
                visualizer.OnQuoteAsync(quote, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        visualizer.OnStopAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return Finish(context, data);
    }

    private static (DriveContext Context, Feed Data) Build(StrategyParameterSchema schema, double[]? closes)
    {
        // Only the instrument is overridden; everything else takes its declared default, converted by
        // the host's own SandboxParameters rather than by a second set of rules that would drift.
        var values = schema.Parameters.ToDictionary(
            p => p.Key,
            object? (p) => p.Kind == ParameterKind.Instrument ? Instrument : p.Default);

        // One feed, shared. Two would leave the context reading a series the caller never advanced —
        // the unit would see an empty history for every bar and every warm-up guard would hold forever.
        var feed = new Feed(closes ?? DefaultCloses);
        return (new DriveContext(feed, new TradingTerminal.Sandbox.SandboxParameters(schema, values)), feed);
    }

    private static Result Finish(DriveContext context, Feed data) =>
        new(context.Recorded, context.Recorder, data.Instruments, Completed: true);

    /// <summary>Bars revealed one at a time, so "recent" history means what had actually arrived. Handing
    /// a unit the whole series up front hides every warm-up bug there is.</summary>
    private sealed class Feed(double[] closes) : IMarketDataView
    {
        private readonly List<OhlcvBar> _seen = [];

        public IReadOnlyList<OhlcvBar> Series { get; } =
            [.. closes.Select((close, index) => new OhlcvBar(
                Instrument, BarSize.OneMinute, Epoch.AddMinutes(index),
                close, close, close, close, index + 1, BrokerKind.Simulated, IsFinal: true))];

        public void Reveal(OhlcvBar bar) => _seen.Add(bar);

        public IEnumerable<Quote> QuotesFor(OhlcvBar bar) =>
        [
            new(Instrument, bar.OpenTimeUtc, bar.OpenTimeUtc,
                bar.Close - 0.5d, bar.Close + 0.5d, 10, 10,
                BrokerKind.Simulated, bar.Volume, EventTimeApproximate: false),
        ];

        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

        public IReadOnlySet<InstrumentId> Instruments { get; } = new HashSet<InstrumentId> { Instrument };

        public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount) =>
            instrument == Instrument && size == BarSize.OneMinute ? [.. _seen.TakeLast(maxCount)] : [];

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) =>
            _seen.Count == 0 ? [] : [.. QuotesFor(_seen[^1])];

        public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount) => [];

        public DepthSnapshot? LatestDepth(InstrumentId instrument) => null;
    }

    private sealed class DriveContext : IStrategyRuntimeContext, IVisualizerContext
    {
        internal DriveContext(IMarketDataView data, IParameters parameters)
        {
            Data = data;
            Recorded = new RecordingParameters(parameters);
            Recorder = new RecordingVirtualBook();
        }

        internal RecordingParameters Recorded { get; }

        internal RecordingVirtualBook Recorder { get; }

        public IMarketDataView Data { get; }

        public IClock Clock { get; } = new FrozenClock();

        public IParameters Parameters => Recorded;

        public IVirtualBook Book => Recorder;

        public IAlertSink Alerts { get; } = new DiscardingAlerts();
    }

    private sealed class FrozenClock : IClock
    {
        public DateTime UtcNow => Epoch;
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
