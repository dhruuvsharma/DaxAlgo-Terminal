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

    /// <summary>
    /// The other instruments in the drive's universe — an index's constituents, a pair's second leg, a
    /// basket.
    ///
    /// <para><b>The third instance of one defect.</b> The drive fed no depth and no tape until
    /// 2026-08-31, and no TIME until 2026-09-01, and each time a whole class of unit was judged
    /// without the drive ever reaching the code it was judging. This is the same shape: the view
    /// answered for exactly one instrument, so a regime screen over an index — one of the three
    /// strategies named as the bar — drew its warm-up message forever and rung 7 reported
    /// <c>draw.text-only</c> against a unit that may be perfectly correct.</para>
    ///
    /// <para>That is the expensive direction. A false rung failure sends a repair agent to rewrite
    /// working code, and teaches the router that the agent who wrote it is unreliable.</para>
    ///
    /// <para>Four of them, and deliberately NOT copies of the primary. A matrix whose rows are
    /// identical has every correlation at one and every ranking arbitrary, which makes a unit that
    /// computes them wrongly draw the same picture as one that computes them correctly — the same
    /// reason the synthetic book is lopsided rather than symmetric.</para>
    /// </summary>
    public static readonly IReadOnlyList<InstrumentId> Peers =
        [new(2), new(3), new(4), new(5)];

    /// <summary>Every instrument the drive serves: the primary first, then its peers.</summary>
    public static IReadOnlyList<InstrumentId> Universe { get; } = [Instrument, .. Peers];

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Down, flat, then up — one hundred and twenty bars.
    ///
    /// <para>The length is not arbitrary. A strategy takes its <b>declared defaults</b> here, so a short
    /// series leaves it still warming up when the drive ends. It then reads none of its risk
    /// parameters, takes no position, and draws nothing — and the ladder reports three failures for a
    /// strategy that was never given enough data to start. A drive that cannot reach the code cannot be
    /// evidence about the code.</para>
    ///
    /// <para><b>Three hundred and twenty, raised from a hundred and twenty on 2026-09-02.</b> That was
    /// sized for a thirty-bar average, and a generated regime screen declared <c>WarmupBars = 200</c> —
    /// which is not greedy, it is the most canonical long lookback there is. It never warmed up, drew
    /// its warm-up message across all three panels, and rung 7 reported <c>draw.text-only</c> three
    /// times against a unit that was correct.</para>
    ///
    /// <para>Worth recording how that was found, because I got it wrong twice first: I predicted the
    /// cause was the drive serving one instrument, fixed that, re-judged, and it still failed;
    /// predicted it was every Instrument parameter resolving to the same id, fixed that, re-judged, and
    /// it still failed. Only then did I read the unit's own constant. Both fixes were real and both
    /// stand — neither was THIS.</para>
    ///
    /// <para>The shape matters too: a sustained fall then a sustained rise guarantees a crossing for any
    /// ordinary pair of periods, and the flat stretch in the middle is a zero-width range — the
    /// commonest source of a NaN in a first draft.</para>
    /// </summary>
    public static readonly double[] DefaultCloses = BuildDefaultSeries();

    private static double[] BuildDefaultSeries()
    {
        var closes = new List<double>(320);
        for (var i = 0; i < 120; i++) closes.Add(130d - i * 0.25d);     // falling
        for (var i = 0; i < 40; i++) closes.Add(closes[^1]);            // flat: a zero-width range
        for (var i = 0; i < 160; i++) closes.Add(closes[^1] + 0.3d);    // rising
        return [.. closes];
    }

    /// <summary>What one drive produced.</summary>
    public sealed record Result(
        RecordingParameters Parameters,
        RecordingVirtualBook Book,
        IReadOnlySet<InstrumentId> Instruments,
        bool Completed);

    /// <summary>
    /// Real market data, captured from a venue and replayed through this drive.
    ///
    /// <para><b>Why replay rather than stream.</b> A live socket makes the lifecycle
    /// non-deterministic and the picture unrepeatable, and it puts a network dependency inside the
    /// verification ladder. Capturing once and replaying gives the unit genuine prices, a genuine
    /// book and a genuine tape - the thing synthetic data cannot be judged for - while every rung
    /// stays reproducible.</para>
    ///
    /// <para>Everything must carry <see cref="Instrument"/> as its id — the PRIMARY one. A capture is
    /// one venue's stream for one symbol, so it fills the primary series; the peers stay synthetic and
    /// keep their derived shapes, which is what a basket unit needs to rank anything.</para>
    /// </summary>
    public sealed record CapturedMarket(
        IReadOnlyList<OhlcvBar> Bars,
        IReadOnlyList<Quote> Quotes,
        IReadOnlyList<TradePrint> Trades,
        IReadOnlyList<DepthSnapshot> Depth);

    /// <summary>Drives a strategy kernel through its full lifecycle.</summary>
    public static Result Run(IStrategyKernel kernel, double[]? closes = null) =>
        Run(kernel, closes, capture: null);

    /// <summary>Drives a kernel against real captured market data.</summary>
    public static Result Run(IStrategyKernel kernel, CapturedMarket capture) =>
        Run(kernel, closes: null, capture);

    private static Result Run(IStrategyKernel kernel, double[]? closes, CapturedMarket? capture)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var (context, data) = Build(kernel.Schema, closes, kernel.DataRequirement, capture);
        kernel.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        var peers = Peers.ToDictionary(p => p, data.PeerSeries);

        for (var step = 0; step < data.Series.Count; step++)
        {
            var bar = data.Series[step];
            data.Reveal(bar);
            kernel.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();

            // The peers, at the same instant. Delivered rather than merely answerable: a view a unit
            // can query but a drive never fills is the same defect as a stream nobody publishes, and
            // this drive has now had that defect three times.
            foreach (var series in peers.Values)
            {
                if (step >= series.Count) continue;

                data.RevealPeer(series[step]);
                kernel.OnBarAsync(series[step], context, CancellationToken.None).GetAwaiter().GetResult();
            }
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
    public static Result Run(IVisualizer visualizer, double[]? closes = null) =>
        Run(visualizer, closes, capture: null);

    /// <summary>Drives a visualizer against real captured market data.</summary>
    public static Result Run(IVisualizer visualizer, CapturedMarket capture) =>
        Run(visualizer, closes: null, capture);

    private static Result Run(IVisualizer visualizer, double[]? closes, CapturedMarket? capture)
    {
        ArgumentNullException.ThrowIfNull(visualizer);

        var (context, data) = Build(visualizer.Schema, closes, visualizer.DataRequirement, capture);
        visualizer.OnStartAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        var peers = Peers.ToDictionary(p => p, data.PeerSeries);

        for (var step = 0; step < data.Series.Count; step++)
        {
            var bar = data.Series[step];
            data.Reveal(bar);
            visualizer.OnBarAsync(bar, context, CancellationToken.None).GetAwaiter().GetResult();

            // The peers, at the same instant. Delivered rather than merely answerable: a view a unit
            // can query but a drive never fills is the same defect as a stream nobody publishes, and
            // this drive has now had that defect three times.
            foreach (var series in peers.Values)
            {
                if (step >= series.Count) continue;

                data.RevealPeer(series[step]);
                visualizer.OnBarAsync(series[step], context, CancellationToken.None).GetAwaiter().GetResult();
            }
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
        StrategyParameterSchema schema, double[]? closes, StrategyDataRequirement requirement,
        CapturedMarket? capture = null)
    {
        // Only the instrument is overridden; everything else takes its declared default, converted by
        // the host's own SandboxParameters rather than by a second set of rules that would drift.
        // A DISTINCT instrument per Instrument parameter, primary first.
        //
        // Every one of them used to get the primary, which looks harmless until a basket unit declares
        // seven -- a regime screen over an index does exactly that, one parameter per constituent --
        // and is then handed seven copies of one symbol. It has nothing to rank, draws its warm-up
        // message forever, and rung 7 reports draw.text-only against a unit doing precisely what the
        // brief asked.
        //
        // Found by predicting that delivering peer bars would fix that unit, re-judging it, and
        // watching it fail anyway. The bars were arriving; the unit was looking for them under an id
        // nobody had given it.
        var slot = 0;
        var values = schema.Parameters.ToDictionary(
            p => p.Key,
            object? (p) => p.Kind == ParameterKind.Instrument ? NextInstrument(ref slot) : p.Default);

        // One feed, shared. Two would leave the context reading a series the caller never advanced —
        // the unit would see an empty history for every bar and every warm-up guard would hold forever.
        var feed = new Feed(closes ?? DefaultCloses, requirement, capture, DeclaredBarSize(schema));
        return (new DriveContext(feed, new TradingTerminal.Sandbox.SandboxParameters(schema, values)), feed);
    }

    /// <summary>The next instrument in the universe, wrapping once they run out — a unit declaring
    /// more members than the drive has peers gets duplicates rather than nothing, which is a worse
    /// picture but still a picture.</summary>
    private static InstrumentId NextInstrument(ref int slot) => Universe[slot++ % Universe.Count];

    /// <summary>
    /// The bar size the unit DECLARED, so the series is stamped with the one it is filtering for.
    ///
    /// <para>The drive stamped every bar OneMinute. A unit that declares a bar-size parameter and
    /// guards <c>bar.Size != _barSize</c> — which is correct of it, since a mixed feed would otherwise
    /// poison its horizon — then dropped every bar the drive sent. An Opus 5 regime screen defaulting
    /// to <c>OneDay</c> drew "no member has enough OneDay bars" across the whole window.</para>
    ///
    /// <para>Read from the declared DEFAULT, exactly as the instrument is, so the drive honours what
    /// the unit asked for rather than what the drive happens to prefer.</para>
    /// </summary>
    private static BarSize DeclaredBarSize(StrategyParameterSchema schema)
    {
        foreach (var parameter in schema.Parameters)
        {
            if (parameter.Default is BarSize declared) return declared;
        }

        return BarSize.OneMinute;
    }

    private static Result Finish(DriveContext context, Feed data) =>
        new(context.Recorded, context.Recorder, data.Instruments, Completed: true);

    /// <summary>Bars revealed one at a time, so "recent" history means what had actually arrived. Handing
    /// a unit the whole series up front hides every warm-up bug there is.</summary>
    private sealed class Feed(
        double[] closes, StrategyDataRequirement requirement, CapturedMarket? capture,
        BarSize barSize = BarSize.OneMinute)
        : IMarketDataView
    {
        /// <summary>Levels per side. Deep enough that a sweep of a few hundred lots walks the book
        /// rather than exhausting it, which is the case a slippage calculation gets wrong.</summary>
        private const int BookLevels = 10;

        private const int PrintsPerBar = 3;

        private readonly List<OhlcvBar> _seen = [];
        private readonly Dictionary<int, List<OhlcvBar>> _peerSeen = [];
        private readonly List<TradePrint> _prints = [];
        private DepthSnapshot? _depth;

        private readonly bool _wantsDepth = requirement.HasFlag(StrategyDataRequirement.Depth);
        private readonly bool _wantsTape = requirement.HasFlag(StrategyDataRequirement.TradeTape);

        public IReadOnlyList<OhlcvBar> Series { get; } = capture is not null
            ? capture.Bars
            : [.. closes.Select((close, index) => new OhlcvBar(
                Instrument, barSize, Epoch.AddMinutes(index),
                close, close, close, close, index + 1, BrokerKind.Simulated, IsFinal: true))];

        /// <summary>
        /// One series per peer, each a different shape.
        ///
        /// <para>Phase-shifted and scaled off the primary rather than copied: a screen that ranks its
        /// rows needs them to actually differ, and identical rows make a correct ranking and a broken
        /// one draw the same picture. The shift is a whole number of bars so the timestamps still line
        /// up, which is what lets a unit compare them at all.</para>
        /// </summary>
        public IReadOnlyList<OhlcvBar> PeerSeries(InstrumentId peer)
        {
            var rank = -1;
            for (var i = 0; i < Peers.Count; i++)
            {
                if (Peers[i] != peer) continue;
                rank = i;
                break;
            }

            if (rank < 0) return [];

            var shift = (rank + 1) * 7;
            var scale = 1d + ((rank + 1) * 0.35d);

            var bars = new OhlcvBar[Series.Count];
            for (var index = 0; index < Series.Count; index++)
            {
                var source = Series[(index + shift) % Series.Count];
                var close = source.Close * scale;

                bars[index] = new OhlcvBar(
                    peer, barSize, Series[index].OpenTimeUtc,
                    close, close, close, close, source.Volume, BrokerKind.Simulated, IsFinal: true);
            }

            return bars;
        }

        /// <summary>Marks a peer bar as arrived, so `RecentBars` for it grows the same way the
        /// primary's does — a unit that reads a hundred bars of history on the first bar of the
        /// session is reading a future the live feed will not give it.</summary>
        public void RevealPeer(OhlcvBar bar)
        {
            if (!_peerSeen.TryGetValue(bar.InstrumentId.Value, out var seen))
                _peerSeen[bar.InstrumentId.Value] = seen = [];

            seen.Add(bar);
        }

        /// <summary>
        /// The window a bar owns: from its open to the next open, and open-ended for the last one so
        /// nothing captured after the final bar closed is silently dropped.
        /// </summary>
        private (DateTime From, DateTime To) Window(OhlcvBar bar)
        {
            // By time rather than by position, so it does not depend on the caller handing back the
            // very instance from Series — and so a duplicate open time cannot pick the wrong window.
            var to = DateTime.MaxValue;
            foreach (var candidate in Series)
            {
                if (candidate.OpenTimeUtc <= bar.OpenTimeUtc) continue;
                to = candidate.OpenTimeUtc;
                break;
            }

            return (bar.OpenTimeUtc, to);
        }

        private IEnumerable<T> Within<T>(OhlcvBar bar, IReadOnlyList<T> events, Func<T, DateTime> at)
        {
            var (from, to) = Window(bar);
            foreach (var item in events)
            {
                var when = at(item);
                if (when >= from && when < to) yield return item;
            }
        }

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

        public IEnumerable<Quote> QuotesFor(OhlcvBar bar) => capture is not null
            ? Within(bar, capture.Quotes, q => q.EventTimeUtc)
            :
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

            if (capture is not null)
            {
                // The newest book inside the bar, because that is the one still standing when the bar
                // closed. Keeping an older one would show a book the market had already moved past.
                foreach (var snapshot in Within(bar, capture.Depth, d => d.TimestampUtc)) _depth = snapshot;
                return _depth;
            }

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

            if (capture is not null)
            {
                foreach (var print in Within(bar, capture.Trades, t => t.EventTimeUtc))
                {
                    _prints.Add(print);
                    yield return print;
                }

                yield break;
            }

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

        public IReadOnlySet<InstrumentId> Instruments { get; } = new HashSet<InstrumentId>(Universe);

        public IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount)
        {
            // ANY bar size, not just the one the drive happens to stamp.
            //
            // The fourth instance of the same defect, found the same way as the other three — by
            // looking at a real unit and asking why it drew nothing. An Opus 5 regime screen asked for
            // OneDay bars, which is an entirely ordinary choice for an index screen, and got an empty
            // list forever: "no member has enough OneDay bars for a 20-bar horizon yet."
            //
            // The series is a series. Refusing it because the caller named a different interval tests
            // the drive's stamping rather than the unit, and a drive that cannot reach the code cannot
            // be evidence about the code.
            if (instrument == Instrument) return [.. _seen.TakeLast(maxCount)];

            return _peerSeen.TryGetValue(instrument.Value, out var seen)
                ? [.. seen.TakeLast(maxCount)]
                : [];
        }

        public IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount) =>
            _seen.Count == 0 ? [] : [.. QuotesFor(_seen[^1]).TakeLast(maxCount)];

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
