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

        var (context, data) = Build(kernel.Schema, closes, kernel.DataRequirement);
        kernel.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Series)
        {
            data.Reveal(bar);
            kernel.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var quote in data.QuotesFor(bar))
                kernel.OnQuoteAsync(quote, context, CancellationToken.None).GetAwaiter().GetResult();
            if (data.DepthFor(bar) is { } depth)
                kernel.OnDepthAsync(Instrument, depth, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var print in data.TradesFor(bar))
                kernel.OnTradeAsync(print, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        kernel.OnStopAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return Finish(context, data);
    }

    /// <summary>Drives a visualizer through its full lifecycle.</summary>
    public static Result Run(IVisualizer visualizer, double[]? closes = null)
    {
        ArgumentNullException.ThrowIfNull(visualizer);

        var (context, data) = Build(visualizer.Schema, closes, visualizer.DataRequirement);
        visualizer.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var bar in data.Series)
        {
            data.Reveal(bar);
            visualizer.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var quote in data.QuotesFor(bar))
                visualizer.OnQuoteAsync(quote, context, CancellationToken.None).GetAwaiter().GetResult();
            if (data.DepthFor(bar) is { } depth)
                visualizer.OnDepthAsync(Instrument, depth, context, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var print in data.TradesFor(bar))
                visualizer.OnTradeAsync(print, context, CancellationToken.None).GetAwaiter().GetResult();
        }

        visualizer.OnStopAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        return Finish(context, data);
    }

    /// <param name="requirement">
    /// What the unit declared it consumes, and therefore what the drive supplies.
    ///
    /// <para>The drive fed bars and quotes and nothing else until 2026-08-31, which meant an order
    /// book, a footprint or an imbalance monitor — the whole class that lives on depth and the tape —
    /// was judged without ever entering the callbacks it is written in. Rung 6 passed by not looking;
    /// rung 7 passed on the unit's own "waiting for depth" message, which is the same frame a
    /// completely broken one draws; and rung 5 <b>failed</b> a correct unit whose parameters are read
    /// where its data arrives. That last one is the expensive direction — it sends a repair agent to
    /// rewrite working code and teaches the router that the agent who wrote it is unreliable.</para>
    ///
    /// <para>It was found by driving the order-flow exemplar, <c>BookPressureVisualizer</c>, through
    /// the ladder for the first time: the worked example a model is shown for every order-flow brief
    /// did not clear rung 7, because the drive gave it nothing to draw.</para>
    ///
    /// <para>Shaped by the declaration rather than always on, so a bar strategy is not handed a stream
    /// it never asked for — which would be a different way of testing something other than the unit.</para>
    /// </param>
    private static (DriveContext Context, Feed Data) Build(
        StrategyParameterSchema schema, double[]? closes, StrategyDataRequirement requirement)
    {
        // Only the instrument is overridden; everything else takes its declared default, converted by
        // the host's own SandboxParameters rather than by a second set of rules that would drift.
        var values = schema.Parameters.ToDictionary(
            p => p.Key,
            object? (p) => p.Kind == ParameterKind.Instrument ? Instrument : p.Default);

        // One feed, shared. Two would leave the context reading a series the caller never advanced —
        // the unit would see an empty history for every bar and every warm-up guard would hold forever.
        var feed = new Feed(closes ?? DefaultCloses, requirement);
        return (new DriveContext(feed, new TradingTerminal.Sandbox.SandboxParameters(schema, values)), feed);
    }

    private static Result Finish(DriveContext context, Feed data) =>
        new(context.Recorded, context.Recorder, data.Instruments, Completed: true);

    /// <summary>Bars revealed one at a time, so "recent" history means what had actually arrived. Handing
    /// a unit the whole series up front hides every warm-up bug there is.</summary>
    private sealed class Feed(double[] closes, StrategyDataRequirement requirement) : IMarketDataView
    {
        /// <summary>Levels per side. Deep enough that a sweep of a few hundred lots walks the book
        /// rather than exhausting it, which is the case a slippage calculation gets wrong.</summary>
        private const int BookLevels = 10;

        private const int PrintsPerBar = 3;

        private readonly List<OhlcvBar> _seen = [];
        private readonly List<TradePrint> _prints = [];
        private DepthSnapshot? _depth;

        private readonly bool _wantsDepth = requirement.HasFlag(StrategyDataRequirement.Depth);
        private readonly bool _wantsTape = requirement.HasFlag(StrategyDataRequirement.TradeTape);

        public IReadOnlyList<OhlcvBar> Series { get; } =
            [.. closes.Select((close, index) => new OhlcvBar(
                Instrument, BarSize.OneMinute, Epoch.AddMinutes(index),
                close, close, close, close, index + 1, BrokerKind.Simulated, IsFinal: true))];

        public void Reveal(OhlcvBar bar) => _seen.Add(bar);

        /// <summary>
        /// The instant the drive has reached — the open time of the newest revealed bar.
        ///
        /// <para><b>The drive supplied data and not time until 2026-09-01.</b> Its clock was frozen at
        /// the epoch while its bars marched a minute apart, so a hundred and twenty bars of market data
        /// arrived in zero seconds. A unit that buckets by wall clock — a liquidity heatmap slicing
        /// every second, which is the ordinary way to build one — closed no bucket in the whole drive
        /// and drew its warm-up message forever. Rung 7 then reported a blank panel for a unit that
        /// would paint perfectly against a real feed, which is the expensive direction: it sends a
        /// repair agent to rewrite working code. Exactly the same omission as feeding no depth and no
        /// tape, one file over.</para>
        /// </summary>
        public DateTime Now => _seen.Count == 0 ? Epoch : _seen[^1].OpenTimeUtc;

        public IEnumerable<Quote> QuotesFor(OhlcvBar bar) =>
        [
            new(Instrument, bar.OpenTimeUtc, bar.OpenTimeUtc,
                bar.Close - 0.5d, bar.Close + 0.5d, 10, 10,
                BrokerKind.Simulated, bar.Volume, EventTimeApproximate: false),
        ];

        /// <summary>
        /// A book around the bar close, or null when the unit never asked for depth.
        ///
        /// <para><b>Deliberately lopsided, and lopsided the other way on alternate bars.</b> A
        /// symmetric book makes every imbalance exactly zero and every microprice exactly the mid, so
        /// a unit computing them wrongly — or not at all — draws the same picture as one computing
        /// them correctly. A drive has to be hostile in the ways the data is.</para>
        /// </summary>
        public DepthSnapshot? DepthFor(OhlcvBar bar)
        {
            if (!_wantsDepth) return null;

            var heavyBid = _seen.Count % 2 == 0;
            var bids = new List<DepthLevel>(BookLevels);
            var asks = new List<DepthLevel>(BookLevels);

            for (var level = 0; level < BookLevels; level++)
            {
                // Size decays away from the touch, which is what a real book does and what makes a
                // sweep price a walk rather than one multiplication.
                var decay = 1d - level / (double)(BookLevels + 2);
                bids.Add(new DepthLevel(
                    bar.Close - 0.5d - level, (long)(100d * decay * (heavyBid ? 1.8d : 0.6d))));
                asks.Add(new DepthLevel(
                    bar.Close + 0.5d + level, (long)(100d * decay * (heavyBid ? 0.6d : 1.8d))));
            }

            _depth = new DepthSnapshot(bar.OpenTimeUtc, bids, asks);
            return _depth;
        }

        /// <summary>
        /// Prints for one bar, or none when the unit never asked for the tape. Both aggressor sides
        /// appear, because a tape that only ever lifts the offer makes signed flow indistinguishable
        /// from gross volume and hides a sign error completely.
        /// </summary>
        public IEnumerable<TradePrint> TradesFor(OhlcvBar bar)
        {
            if (!_wantsTape) yield break;

            for (var i = 0; i < PrintsPerBar; i++)
            {
                var buy = (bar.Volume + i) % 2 == 0;
                var print = new TradePrint(
                    Instrument,
                    bar.OpenTimeUtc.AddSeconds(i * 15),
                    bar.OpenTimeUtc.AddSeconds(i * 15),
                    buy ? bar.Close + 0.5d : bar.Close - 0.5d,
                    10 + i * 5,
                    buy ? AggressorSide.Buy : AggressorSide.Sell,
                    BrokerKind.Simulated,
                    bar.Volume * PrintsPerBar + i,
                    EventTimeApproximate: false);

                _prints.Add(print);
                yield return print;
            }
        }

        /// <summary>What the drive actually supplies. A view claiming more than it serves would leave
        /// a unit waiting forever on a stream that never arrives.</summary>
        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1
            | StrategyDataRequirement.Bars
            | (_wantsDepth ? StrategyDataRequirement.Depth : 0)
            | (_wantsTape ? StrategyDataRequirement.TradeTape : 0);

        public IReadOnlySet<InstrumentId> Instruments { get; } = new HashSet<InstrumentId> { Instrument };

        public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount) =>
            instrument == Instrument && size == BarSize.OneMinute ? [.. _seen.TakeLast(maxCount)] : [];

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) =>
            _seen.Count == 0 ? [] : [.. QuotesFor(_seen[^1])];

        public IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount) =>
            instrument == Instrument ? [.. _prints.TakeLast(maxCount)] : [];

        public DepthSnapshot? LatestDepth(InstrumentId instrument) =>
            instrument == Instrument ? _depth : null;
    }

    private sealed class DriveContext : IStrategyRuntimeContext, IVisualizerContext
    {
        internal DriveContext(Feed data, IParameters parameters)
        {
            Data = data;
            Clock = new DriveClock(data);
            Recorded = new RecordingParameters(parameters);
            Recorder = new RecordingVirtualBook();
        }

        internal RecordingParameters Recorded { get; }

        internal RecordingVirtualBook Recorder { get; }

        public IMarketDataView Data { get; }

        public IClock Clock { get; }

        public IParameters Parameters => Recorded;

        public IVirtualBook Book => Recorder;

        public IAlertSink Alerts { get; } = new DiscardingAlerts();
    }

    /// <summary>The drive's own clock: it advances with the bars, so a unit that buckets by wall
    /// clock closes buckets here the way it would against a feed. Deterministic — it is the bar
    /// series, not the machine's clock — so two runs of the same unit still produce the same
    /// verdict.</summary>
    private sealed class DriveClock(Feed feed) : IClock
    {
        public DateTime UtcNow => feed.Now;
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
