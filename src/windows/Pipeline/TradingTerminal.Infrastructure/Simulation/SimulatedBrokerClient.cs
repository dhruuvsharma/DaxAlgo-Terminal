using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Simulation;

/// <summary>
/// In-process <see cref="IBrokerClient"/> with no broker and no network — the backend behind
/// <see cref="BrokerKind.Simulated"/>. Two feed modes (see <see cref="SimulatedBrokerOptions"/>):
/// <list type="bullet">
/// <item><b>Synthetic</b> — a deterministic random walk, so the whole app can run fully offline
/// with zero recorded data.</item>
/// <item><b>Replay</b> — streams recorded data out of <see cref="IMarketDataStore"/> on a
/// speed-scaled clock, re-emitting it as if it were arriving live. Falls back to the synthetic
/// feed for any instrument/stream the store has no data for, so a window is never just empty.</item>
/// </list>
/// Everything downstream (ingest → hub → strategies/tools) consumes this exactly like a real
/// broker. Trade tape and L2 depth are both supported here (unlike the NT/cTrader/Alpaca
/// backends, which throw for the channels they don't wire).
/// </summary>
internal sealed class SimulatedBrokerClient : IBrokerClient
{
    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly SimulatedBrokerOptions _options;
    private readonly ILogger<SimulatedBrokerClient> _logger;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public SimulatedBrokerClient(
        IMarketDataStore store,
        IInstrumentRegistry registry,
        IOptions<SimulatedBrokerOptions> options,
        ILogger<SimulatedBrokerClient> logger)
    {
        _store = store;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public BrokerKind Kind => BrokerKind.Simulated;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Simulated broker connected ({Mode} mode, speed x{Speed}).", _options.Mode, _options.SpeedMultiplier);
        _state.OnNext(Core.Domain.ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        // Replay: surface whatever the store already holds (so picking one resolves to real data).
        // Synthetic: surface the configured symbol list.
        if (_options.Mode == SimulatedFeedMode.Replay)
        {
            var fromStore = _registry.All()
                .Select(i => new TradableInstrument(
                    i.CanonicalSymbol, $"Simulated · {i.AssetClass}", ContractFor(i), BrokerKind.Simulated))
                .ToList();
            if (fromStore.Count > 0)
                return Task.FromResult<IReadOnlyList<TradableInstrument>>(fromStore);
        }

        var synthetic = _options.Instruments
            .Select(s => new TradableInstrument(s, "Simulated", SyntheticContract(s), BrokerKind.Simulated))
            .ToList();
        return Task.FromResult<IReadOnlyList<TradableInstrument>>(synthetic);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var step = barSize.ToTimeSpan();
        var count = Math.Clamp((int)(duration.Ticks / Math.Max(1, step.Ticks)), 1, 1000);

        if (_options.Mode == SimulatedFeedMode.Replay && ResolveStored(contract) is { } id)
        {
            var stored = await _store.GetRecentBarsAsync(id, barSize, count, source: null, ct).ConfigureAwait(false);
            if (stored.Count > 0)
                return stored.Select(b => b.ToBar()).ToList();
            _logger.LogInformation(
                "Simulated replay: no stored {Size} bars for {Symbol}; serving synthetic history.",
                barSize, contract.Symbol);
        }

        // Synthetic history: walk backwards from now so the series ends at the current bar.
        var walk = NewWalk(contract.Symbol, "histbars");
        var bars = new List<Bar>(count);
        var end = DateTime.UtcNow;
        for (var i = count - 1; i >= 0; i--)
            bars.Add(walk.NextBar(end - step * i, _options.SyntheticVolatility));
        return bars;
    }

    // ---- Streaming -----------------------------------------------------------------------

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = _options.Mode == SimulatedFeedMode.Replay ? ResolveStored(contract) : null;
        if (id is null)
        {
            await foreach (var b in SyntheticBars(contract, barSize, ct).ConfigureAwait(false))
                yield return b;
            yield break;
        }

        var (from, to) = await WindowAsync(ct).ConfigureAwait(false);
        var any = false;
        do
        {
            await using var e = _store.ReadBarsAsync(id.Value, barSize, from, to, source: null, ct).GetAsyncEnumerator(ct);
            DateTime? prev = null;
            while (true)
            {
                bool has;
                try { has = await e.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                if (!has) break;
                var ob = e.Current;
                if (await DelayBetweenAsync(prev, ob.OpenTimeUtc, ct).ConfigureAwait(false)) yield break;
                prev = ob.OpenTimeUtc;
                any = true;
                yield return ob.ToBar();
            }
        }
        while (_options.Loop && any && !ct.IsCancellationRequested);

        if (!any)
        {
            LogNoData("bars", contract.Symbol);
            await foreach (var b in SyntheticBars(contract, barSize, ct).ConfigureAwait(false))
                yield return b;
        }
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = _options.Mode == SimulatedFeedMode.Replay ? ResolveStored(contract) : null;
        if (id is null)
        {
            await foreach (var t in SyntheticTicks(contract, ct).ConfigureAwait(false)) yield return t;
            yield break;
        }

        var (from, to) = await WindowAsync(ct).ConfigureAwait(false);
        var any = false;
        do
        {
            await using var e = _store.ReadQuotesAsync(id.Value, from, to, source: null, ct).GetAsyncEnumerator(ct);
            DateTime? prev = null;
            while (true)
            {
                bool has;
                try { has = await e.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                if (!has) break;
                var q = e.Current;
                if (await DelayBetweenAsync(prev, q.EventTimeUtc, ct).ConfigureAwait(false)) yield break;
                prev = q.EventTimeUtc;
                any = true;
                yield return new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize);
            }
        }
        while (_options.Loop && any && !ct.IsCancellationRequested);

        if (!any)
        {
            LogNoData("quotes", contract.Symbol);
            await foreach (var t in SyntheticTicks(contract, ct).ConfigureAwait(false)) yield return t;
        }
    }

    public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = _options.Mode == SimulatedFeedMode.Replay ? ResolveStored(contract) : null;
        if (id is null)
        {
            await foreach (var t in SyntheticTrades(contract, ct).ConfigureAwait(false)) yield return t;
            yield break;
        }

        var (from, to) = await WindowAsync(ct).ConfigureAwait(false);
        var any = false;
        do
        {
            await using var e = _store.ReadTradesAsync(id.Value, from, to, source: null, ct).GetAsyncEnumerator(ct);
            DateTime? prev = null;
            while (true)
            {
                bool has;
                try { has = await e.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                if (!has) break;
                var tp = e.Current;
                if (await DelayBetweenAsync(prev, tp.EventTimeUtc, ct).ConfigureAwait(false)) yield break;
                prev = tp.EventTimeUtc;
                any = true;
                yield return new TradeTick(tp.EventTimeUtc, tp.Price, tp.Size, tp.Aggressor);
            }
        }
        while (_options.Loop && any && !ct.IsCancellationRequested);

        if (!any)
        {
            LogNoData("trades", contract.Symbol);
            await foreach (var t in SyntheticTrades(contract, ct).ConfigureAwait(false)) yield return t;
        }
    }

    public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = _options.Mode == SimulatedFeedMode.Replay ? ResolveStored(contract) : null;
        if (id is null)
        {
            await foreach (var d in SyntheticDepth(contract, levels, ct).ConfigureAwait(false)) yield return d;
            yield break;
        }

        var (from, to) = await WindowAsync(ct).ConfigureAwait(false);
        var any = false;
        do
        {
            await using var e = _store.ReadDepthAsync(id.Value, from, to, ct).GetAsyncEnumerator(ct);
            DateTime? prev = null;
            while (true)
            {
                bool has;
                try { has = await e.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                if (!has) break;
                var snap = e.Current;
                if (await DelayBetweenAsync(prev, snap.TimestampUtc, ct).ConfigureAwait(false)) yield break;
                prev = snap.TimestampUtc;
                any = true;
                yield return snap;
            }
        }
        while (_options.Loop && any && !ct.IsCancellationRequested);

        if (!any)
        {
            LogNoData("depth", contract.Symbol);
            await foreach (var d in SyntheticDepth(contract, levels, ct).ConfigureAwait(false)) yield return d;
        }
    }

    // ---- Synthetic generators ------------------------------------------------------------

    private async IAsyncEnumerable<Bar> SyntheticBars(
        Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct)
    {
        var walk = NewWalk(contract.Symbol, "bars");
        var interval = ScaledMs(_options.SyntheticBarIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelledAsync(interval, ct).ConfigureAwait(false)) yield break;
            yield return walk.NextBar(DateTime.UtcNow, _options.SyntheticVolatility);
        }
    }

    private async IAsyncEnumerable<Tick> SyntheticTicks(
        Contract contract, [EnumeratorCancellation] CancellationToken ct)
    {
        var walk = NewWalk(contract.Symbol, "ticks");
        var interval = ScaledMs(_options.SyntheticTickIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelledAsync(interval, ct).ConfigureAwait(false)) yield break;
            var mid = walk.Step(_options.SyntheticVolatility);
            var half = Math.Max(0.01, mid * 0.0001);
            yield return new Tick(DateTime.UtcNow, mid - half, mid + half,
                walk.NextSize(), walk.NextSize());
        }
    }

    private async IAsyncEnumerable<TradeTick> SyntheticTrades(
        Contract contract, [EnumeratorCancellation] CancellationToken ct)
    {
        var walk = NewWalk(contract.Symbol, "trades");
        var interval = ScaledMs(_options.SyntheticTickIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelledAsync(interval, ct).ConfigureAwait(false)) yield break;
            var price = walk.Step(_options.SyntheticVolatility);
            var aggressor = walk.NextBool() ? AggressorSide.Buy : AggressorSide.Sell;
            yield return new TradeTick(DateTime.UtcNow, price, walk.NextSize(), aggressor);
        }
    }

    private async IAsyncEnumerable<DepthSnapshot> SyntheticDepth(
        Contract contract, int levels, [EnumeratorCancellation] CancellationToken ct)
    {
        var walk = NewWalk(contract.Symbol, "depth");
        var interval = ScaledMs(_options.SyntheticTickIntervalMs);
        var tick = Math.Max(0.01, _options.SyntheticStartPrice * 0.0001);
        while (!ct.IsCancellationRequested)
        {
            if (await DelayOrCancelledAsync(interval, ct).ConfigureAwait(false)) yield break;
            var mid = walk.Step(_options.SyntheticVolatility);
            var bids = new List<DepthLevel>(levels);
            var asks = new List<DepthLevel>(levels);
            for (var i = 0; i < levels; i++)
            {
                bids.Add(new DepthLevel(Math.Round(mid - tick * (i + 1), 2), walk.NextSize()));
                asks.Add(new DepthLevel(Math.Round(mid + tick * (i + 1), 2), walk.NextSize()));
            }
            yield return new DepthSnapshot(DateTime.UtcNow, bids, asks);
        }
    }

    // ---- Replay helpers ------------------------------------------------------------------

    /// <summary>Find the stored <see cref="InstrumentId"/> whose canonical symbol matches the
    /// requested contract, regardless of which broker originally recorded it (the store keys on
    /// id only). Null when nothing matching is in the store.</summary>
    private InstrumentId? ResolveStored(Contract contract)
    {
        var match = _registry.All()
            .FirstOrDefault(i => string.Equals(i.CanonicalSymbol, contract.Symbol, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match.Id;
    }

    /// <summary>The replay window: the store's actual data extent when known, else the configured
    /// lookback ending now.</summary>
    private async Task<(DateTime from, DateTime to)> WindowAsync(CancellationToken ct)
    {
        var extent = await _store.GetDataExtentAsync(ct).ConfigureAwait(false);
        if (extent.EarliestUtc is { } earliest && extent.LatestUtc is { } latest && latest > earliest)
            return (earliest, latest);
        var now = DateTime.UtcNow;
        return (now - TimeSpan.FromDays(Math.Max(1, _options.ReplayLookbackDays)), now);
    }

    /// <summary>Sleep the scaled inter-event gap (capped by <c>MaxGapSeconds</c>). Returns true if
    /// cancelled mid-wait.</summary>
    private Task<bool> DelayBetweenAsync(DateTime? prev, DateTime current, CancellationToken ct)
    {
        if (prev is null) return Task.FromResult(ct.IsCancellationRequested);
        var gap = current - prev.Value;
        if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
        var cap = TimeSpan.FromSeconds(Math.Max(0, _options.MaxGapSeconds));
        if (gap > cap) gap = cap;
        return DelayOrCancelledAsync(Scale(gap), ct);
    }

    // ---- Shared utilities ----------------------------------------------------------------

    private TimeSpan Scale(TimeSpan span)
    {
        var speed = _options.SpeedMultiplier <= 0 ? 1.0 : _options.SpeedMultiplier;
        return TimeSpan.FromTicks((long)(span.Ticks / speed));
    }

    private TimeSpan ScaledMs(int ms) => Scale(TimeSpan.FromMilliseconds(Math.Max(1, ms)));

    private static async Task<bool> DelayOrCancelledAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return ct.IsCancellationRequested;
        try { await Task.Delay(delay, ct).ConfigureAwait(false); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private void LogNoData(string stream, string symbol) => _logger.LogInformation(
        "Simulated replay: no stored {Stream} for {Symbol}; serving synthetic feed.", stream, symbol);

    private RandomWalk NewWalk(string symbol, string streamTag)
        => new(unchecked(_options.Seed ^ symbol.GetHashCode() ^ streamTag.GetHashCode()),
               _options.SyntheticStartPrice);

    private static Contract SyntheticContract(string symbol) =>
        new(symbol, "STK", "SIM", "USD", "SIM");

    private static Contract ContractFor(Instrument i) =>
        new(i.CanonicalSymbol, SecTypeFor(i.AssetClass), i.Exchange, i.Currency, i.Exchange);

    private static string SecTypeFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Future => "FUT",
        AssetClass.Forex => "CASH",
        AssetClass.Crypto => "CRYPTO",
        AssetClass.Option => "OPT",
        AssetClass.Index => "IND",
        _ => "STK",
    };

    public ValueTask DisposeAsync()
    {
        _state.OnCompleted();
        _state.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A deterministic geometric random walk with a seeded RNG, shared shape for the
    /// synthetic bar/tick/trade/depth generators.</summary>
    private sealed class RandomWalk(int seed, double startPrice)
    {
        private readonly Random _rng = new(seed);
        private double _price = startPrice <= 0 ? 100.0 : startPrice;

        /// <summary>Advance one step and return the new mid price.</summary>
        public double Step(double vol)
        {
            // Box-Muller normal, scaled by vol as a fraction of price.
            var u1 = 1.0 - _rng.NextDouble();
            var u2 = 1.0 - _rng.NextDouble();
            var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            _price = Math.Max(0.01, _price * (1.0 + z * vol));
            return Math.Round(_price, 2);
        }

        public Bar NextBar(DateTime timestampUtc, double vol)
        {
            var open = _price;
            var a = Step(vol);
            var b = Step(vol);
            var close = _price;
            var high = Math.Max(Math.Max(open, close), Math.Max(a, b));
            var low = Math.Min(Math.Min(open, close), Math.Min(a, b));
            return new Bar(timestampUtc, Math.Round(open, 2), Math.Round(high, 2),
                Math.Round(low, 2), Math.Round(close, 2), NextSize() * 10);
        }

        public long NextSize() => _rng.Next(1, 50);

        public bool NextBool() => _rng.Next(2) == 0;
    }
}
