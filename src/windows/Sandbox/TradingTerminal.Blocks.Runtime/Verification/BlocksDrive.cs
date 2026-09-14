using DaxAlgo.Blocks;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Blocks.Runtime.Verification;

/// <summary>How serious a drive finding is.</summary>
public enum DriveSeverity
{
    /// <summary>Worth knowing; the unit still passes.</summary>
    Warning,

    /// <summary>The unit is broken; it does not pass.</summary>
    Failure,
}

/// <summary>One thing the drive found, with a stable code a repair prompt can be routed by.</summary>
public sealed record DriveFinding(DriveSeverity Severity, string Code, string Message, string? Remedy = null)
{
    public override string ToString() => Remedy is null ? $"[{Code}] {Message}" : $"[{Code}] {Message} {Remedy}";
}

/// <summary>What driving a unit showed.</summary>
public sealed record DriveReport(
    IReadOnlyList<DriveFinding> Findings,
    bool UsesOrders,
    int Fills,
    int PagePosts,
    long HandlerFaults,
    long DroppedMarketEvents,
    IReadOnlyList<InstrumentId> Subscribed,
    TimeSpan Elapsed)
{
    public bool Passed => Findings.All(finding => finding.Severity != DriveSeverity.Failure);
}

/// <summary>How long and how hard to drive.</summary>
/// <param name="Steps">Price updates per subscribed instrument.</param>
/// <param name="HasPage">Whether the unit ships a page, so silence towards it is a failure.</param>
/// <param name="SettleTime">How long timers are given to fire after the feed.</param>
public sealed record DriveOptions(int Steps = 240, bool HasPage = false, TimeSpan? SettleTime = null)
{
    public TimeSpan Settle => SettleTime ?? TimeSpan.FromMilliseconds(600);
}

/// <summary>
/// Runs a compiled unit against a synthetic market and reports what went wrong, for free.
///
/// <para>Deterministic checks before any model is asked for an opinion: did it start, did any handler
/// throw, did it stop cleanly, did it keep up, did it read the settings it declared, and — when it has a
/// page — did it ever send that page anything. Each finding has a stable code so a repair can be routed
/// to whoever owns the failing part.</para>
///
/// <para>The feed is deliberately awkward, the way the sandbox drive is: a trend, a flat stretch, a gap,
/// and a jump, with instruments wobbling out of phase so pairs diverge and converge, on every
/// instrument the unit subscribes to — however many that is, and whenever it subscribed. Quotes,
/// prints, depth and one-minute bars all flow; a unit only receives what it asked for.</para>
/// </summary>
public static class BlocksDrive
{
    public static async Task<DriveReport> RunAsync(Func<IUnit> factory, DriveOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        options ??= new DriveOptions();

        var started = DateTime.UtcNow;
        var findings = new List<DriveFinding>();
        var log = new List<string>();
        var hub = new SyntheticHub();
        var clock = new DriveClock();
        var page = new RecordingPage();

        var host = new BlocksHost(
            hub,
            clock,
            (_, level, message) => { lock (log) log.Add($"{level} {message}"); },
            _ => { });

        await using var runtime = new BlocksUnitRuntime(factory, "drive", host, ui: page);

        try
        {
            await runtime.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            findings.Add(new DriveFinding(DriveSeverity.Failure, "start.threw",
                $"StartAsync threw {ex.GetType().Name}: {ex.Message}",
                "Register handlers and return; do not read data that is not there yet, and do not block."));
            return Report(findings, runtime, page, started, []);
        }

        page.Open();

        for (var step = 0; step < options.Steps && !ct.IsCancellationRequested; step++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var instrument in runtime.SubscribedInstruments)
                hub.Step(instrument, step, clock.UtcNow);

            // Give the unit thread room so the drive measures the unit, not a flood.
            if (step % 20 == 0) await Task.Delay(5, ct).ConfigureAwait(false);
        }

        await Task.Delay(options.Settle, ct).ConfigureAwait(false);

        foreach (var unread in runtime.UnreadSettings)
            findings.Add(new DriveFinding(DriveSeverity.Warning, "settings.unread",
                $"The setting '{unread}' is declared but never read, so its control does nothing.",
                "Read it where it matters, or remove it from Info.Settings."));

        // Read before stopping: a stopped unit has released every stream it held.
        InstrumentId[] subscribed = [.. runtime.SubscribedInstruments];

        await runtime.StopAsync(ct).ConfigureAwait(false);

        string[] lines;
        lock (log) lines = [.. log];

        if (runtime.Faults > 0)
            findings.Add(new DriveFinding(DriveSeverity.Failure, "handler.threw",
                $"{runtime.Faults} handler call(s) threw; the last: {runtime.LastFault}",
                "Guard against data that is not there yet: an empty window, a zero price, a missing depth side."));

        if (lines.FirstOrDefault(l => l.Contains("failed to stop", StringComparison.Ordinal)) is { } stop)
            findings.Add(new DriveFinding(DriveSeverity.Failure, "stop.threw", stop, "StopAsync must not throw."));

        foreach (var refused in lines.Where(l => l.Contains("was refused", StringComparison.Ordinal)).Distinct().Take(3))
            findings.Add(new DriveFinding(DriveSeverity.Warning, "orders.refused", refused));

        if (runtime.DroppedMarketEvents > 0)
            findings.Add(new DriveFinding(DriveSeverity.Warning, "data.dropped",
                $"{runtime.DroppedMarketEvents} market event(s) were dropped because handlers could not keep up.",
                "Keep handlers short: update state, and leave heavy work to a timer."));

        if (options.HasPage && page.Posts == 0)
            findings.Add(new DriveFinding(DriveSeverity.Failure, "ui.never-sent",
                "The page opened and the unit never sent it anything, so the window stays blank.",
                "Send the whole state from context.Ui.OnOpened, and again whenever it changes."));

        return Report(findings, runtime, page, started, subscribed);
    }

    private static DriveReport Report(
        List<DriveFinding> findings, BlocksUnitRuntime runtime, RecordingPage page, DateTime started, IReadOnlyList<InstrumentId> subscribed) =>
        new(findings,
            runtime.UsesOrders,
            runtime.RecentFills(512).Count,
            page.Posts,
            runtime.Faults,
            runtime.DroppedMarketEvents,
            subscribed,
            DateTime.UtcNow - started);

    /// <summary>A market where instruments walk the same awkward path near each other, out of phase.</summary>
    private sealed class SyntheticHub : IMarketDataHub
    {
        private readonly Feed<Quote> _quotes = new();
        private readonly Feed<TradePrint> _trades = new();
        private readonly Feed<OhlcvBar> _bars = new();
        private readonly Feed<(InstrumentId, DepthSnapshot)> _depth = new();
        private readonly Dictionary<InstrumentId, (double Open, double High, double Low, long Volume, DateTime Start)> _forming = new();

        public IObservable<Quote> Quotes(InstrumentId instrumentId) => _quotes;
        public IObservable<TradePrint> Trades(InstrumentId instrumentId) => _trades;
        public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) => _bars;
        public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) => new DepthFeed(_depth, instrumentId);

        public void PublishQuote(Quote quote) => _quotes.Next(quote);
        public void PublishTrade(TradePrint trade) => _trades.Next(trade);
        public void PublishBar(OhlcvBar bar) => _bars.Next(bar);
        public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) => _depth.Next((instrumentId, snapshot));

        public void Step(InstrumentId instrument, int step, DateTime now)
        {
            // Levels close together and a wobble out of phase per instrument, so two instruments move
            // together but not in lockstep. A feed where every instrument walks the identical path never
            // opens a spread, and a drive that can never trigger an arbitrage has not driven one.
            var level = 100d + instrument.Value % 5;
            var price = level * Path(step) * (1d + 0.02 * Math.Sin(step / 7d + instrument.Value * 1.9));
            var tick = Math.Max(0.01, Math.Round(level * 0.0005, 2));
            var bid = Math.Round(price - tick, 2);
            var ask = Math.Round(price + tick, 2);

            PublishQuote(new Quote(instrument, now, now, bid, ask, 5 + step % 7, 4 + step % 5, BrokerKind.Simulated, step, false));
            PublishTrade(new TradePrint(instrument, now, now, step % 2 == 0 ? ask : bid, 1 + step % 9,
                step % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell, BrokerKind.Simulated, step, false));
            PublishDepth(instrument, new DepthSnapshot(now,
                [.. Enumerable.Range(0, 5).Select(i => new DepthLevel(Math.Round(bid - i * tick, 2), 10 + i * 3))],
                [.. Enumerable.Range(0, 5).Select(i => new DepthLevel(Math.Round(ask + i * tick, 2), 8 + i * 4))]));

            (double Open, double High, double Low, long Volume, DateTime Start) bar = _forming.TryGetValue(instrument, out var f)
                ? (f.Open, Math.Max(f.High, price), Math.Min(f.Low, price), f.Volume + 1 + step % 9, f.Start)
                : (price, price, price, 1L, now);

            var final = step % 10 == 9;
            PublishBar(new OhlcvBar(instrument, BarSize.OneMinute, bar.Start, bar.Open, bar.High, bar.Low, price, bar.Volume,
                BrokerKind.Simulated, final));

            if (final) _forming.Remove(instrument);
            else _forming[instrument] = bar;
        }

        /// <summary>Up, flat, a gap down, a recovery, then a jump.</summary>
        private static double Path(int step) => step switch
        {
            < 60 => 1d + step * 0.0015,
            < 100 => 1.09,
            < 101 => 1.02,
            < 180 => 1.02 + (step - 100) * 0.0008,
            < 181 => 1.12,
            _ => 1.12 - (step - 180) * 0.001,
        };
    }

    private sealed class Feed<T> : IObservable<T>
    {
        private readonly object _gate = new();
        private readonly List<IObserver<T>> _observers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            lock (_gate) _observers.Add(observer);
            return new Disposer(() => { lock (_gate) _observers.Remove(observer); });
        }

        public void Next(T value)
        {
            IObserver<T>[] observers;
            lock (_gate) observers = [.. _observers];
            foreach (var observer in observers) observer.OnNext(value);
        }
    }

    private sealed class DepthFeed(Feed<(InstrumentId, DepthSnapshot)> feed, InstrumentId instrument) : IObservable<DepthSnapshot>
    {
        public IDisposable Subscribe(IObserver<DepthSnapshot> observer) => feed.Subscribe(new Relay(observer, instrument));

        private sealed class Relay(IObserver<DepthSnapshot> inner, InstrumentId instrument) : IObserver<(InstrumentId, DepthSnapshot)>
        {
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext((InstrumentId, DepthSnapshot) value) { if (value.Item1 == instrument) inner.OnNext(value.Item2); }
        }
    }

    private sealed class DriveClock : IClock
    {
        private long _ticks = new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc).Ticks;

        public DateTime UtcNow => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class RecordingPage : IUnitUiEndpoint
    {
        private int _posts;

        public bool IsOpen { get; private set; }

        public int Posts => Volatile.Read(ref _posts);

        public event Action<string, string>? MessageReceived;
        public event Action? Opened;

        public void Post(string topic, string json) => Interlocked.Increment(ref _posts);

        public void Open()
        {
            IsOpen = true;
            Opened?.Invoke();
            MessageReceived?.Invoke("drive.ping", "{}");
        }
    }
}
