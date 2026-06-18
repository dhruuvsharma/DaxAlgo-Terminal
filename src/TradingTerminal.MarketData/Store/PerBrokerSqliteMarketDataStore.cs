using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Per-broker, per-stream SQLite <see cref="IMarketDataStore"/>: each broker gets four time-series
/// files — <c>{stem}-{broker}-bars.db</c> (bars), <c>-l1.db</c> (quotes), <c>-trades.db</c> (tape),
/// and <c>-l2.db</c> (depth) — each backed by its own <see cref="SqliteMarketDataStore"/> with an
/// independent connection + background writer. Benefits over a single shared file:
/// <list type="bullet">
///   <item>every (broker, stream) writes in parallel — no single-writer SQLite lock contention;</item>
///   <item>the same instrument's bars from different brokers can't collide (separate files);</item>
///   <item>a broker's history — or just one stream of it — is wiped by deleting a file;</item>
///   <item>L2 depth is persisted (in its own <c>-l2.db</c> file with a startup retention prune),
///         which the single-file SQLite backend deliberately drops.</item>
/// </list>
/// Canonical identity (the <c>instruments</c>/<c>instrument_aliases</c> registry) stays in the shared
/// <c>marketdata.db</c> handled by the registry — these files hold time-series only and store
/// <c>instrument_id</c> as the plain integer the one registry assigned, so <see cref="InstrumentId"/>
/// stays broker-neutral and cross-venue tools keep working.
///
/// <para>Writes route by stream + the record's <c>Source</c>. Reads with a concrete <c>source</c> hit
/// that one file; reads with <c>source: null</c> fan out across every broker's file for that stream
/// and merge ascending. Depth reads always fan out (depth has no source parameter).</para>
/// </summary>
internal sealed class PerBrokerSqliteMarketDataStore : IMarketDataStore, IDisposable
{
    private enum Stream { Quotes, Trades, Bars, Depth }

    private readonly string _baseDirectory;
    private readonly string _fileStem;
    private readonly bool _persist;
    private readonly int _batchSize;
    private readonly int _depthRetentionDays;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(BrokerKind Broker, Stream Stream), Lazy<SqliteMarketDataStore>> _stores = new();

    public PerBrokerSqliteMarketDataStore(
        string baseDirectory, string fileStem, bool persist, int batchSize, int depthRetentionDays, ILoggerFactory loggerFactory)
    {
        _baseDirectory = baseDirectory;
        _fileStem = fileStem;
        _persist = persist;
        _batchSize = batchSize;
        _depthRetentionDays = depthRetentionDays;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("MarketData");
        Directory.CreateDirectory(baseDirectory);

        foreach (var (broker, stream) in DiscoverExistingFiles())
            _ = StoreFor(broker, stream);

        _logger.LogInformation(
            "Market-data store: per-broker SQLite, one file per stream ({Count} existing file(s)) in {Dir}.",
            _stores.Count, baseDirectory);
    }

    // ── Writes (route by stream + source) ──────────────────────────────────────────────────────
    public void EnqueueQuote(Quote quote) => StoreFor(quote.Source, Stream.Quotes).EnqueueQuote(quote);
    public void EnqueueTrade(TradePrint trade) => StoreFor(trade.Source, Stream.Trades).EnqueueTrade(trade);
    public void EnqueueBar(OhlcvBar bar) => StoreFor(bar.Source, Stream.Bars).EnqueueBar(bar);
    public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source) =>
        StoreFor(source, Stream.Depth).EnqueueDepth(instrumentId, snapshot, source);

    public async Task FlushAsync(CancellationToken ct = default) =>
        await Task.WhenAll(All().Select(s => s.FlushAsync(ct))).ConfigureAwait(false);

    // ── Reads ────────────────────────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, BrokerKind? source = null,
        CancellationToken ct = default)
    {
        if (source is { } b)
            return ExistingStoreFor(b, Stream.Bars) is { } store
                ? await store.GetRecentBarsAsync(instrumentId, size, count, b, ct).ConfigureAwait(false)
                : Array.Empty<OhlcvBar>();

        var merged = new Dictionary<DateTime, OhlcvBar>();
        foreach (var store in Open(Stream.Bars))
            foreach (var bar in await store.GetRecentBarsAsync(instrumentId, size, count, null, ct).ConfigureAwait(false))
                merged[bar.OpenTimeUtc] = bar;

        return merged.Values.OrderBy(x => x.OpenTimeUtc).TakeLast(Math.Max(1, count)).ToArray();
    }

    public IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        CancellationToken ct = default) =>
        source is { } b
            ? (ExistingStoreFor(b, Stream.Quotes)?.ReadQuotesAsync(instrumentId, fromUtc, toUtc, b, ct) ?? Empty<Quote>())
            : MergeAscending(Open(Stream.Quotes).Select(s => s.ReadQuotesAsync(instrumentId, fromUtc, toUtc, null, ct)), q => q.EventTimeUtc, ct);

    public IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        CancellationToken ct = default) =>
        source is { } b
            ? (ExistingStoreFor(b, Stream.Trades)?.ReadTradesAsync(instrumentId, fromUtc, toUtc, b, ct) ?? Empty<TradePrint>())
            : MergeAscending(Open(Stream.Trades).Select(s => s.ReadTradesAsync(instrumentId, fromUtc, toUtc, null, ct)), t => t.EventTimeUtc, ct);

    public IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
        BrokerKind? source = null, CancellationToken ct = default) =>
        source is { } b
            ? (ExistingStoreFor(b, Stream.Bars)?.ReadBarsAsync(instrumentId, size, fromUtc, toUtc, b, ct) ?? Empty<OhlcvBar>())
            : MergeAscending(Open(Stream.Bars).Select(s => s.ReadBarsAsync(instrumentId, size, fromUtc, toUtc, null, ct)), x => x.OpenTimeUtc, ct);

    // Depth has no source parameter — always fan out across brokers' -l2.db files, merged by snapshot time.
    public IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        MergeAscending(Open(Stream.Depth).Select(s => s.ReadDepthAsync(instrumentId, fromUtc, toUtc, ct)), d => d.TimestampUtc, ct);

    public async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
    {
        var extent = StoredDataExtent.Empty;
        foreach (var store in All())
            extent = StoredDataExtent.Combine(extent, await store.GetDataExtentAsync(ct).ConfigureAwait(false));
        return extent;
    }

    // ── Range deletes (route by stream, sum rows) ──────────────────────────────────────────────
    public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        SumDeleteAsync(Stream.Quotes, s => s.DeleteQuotesInRangeAsync(fromUtc, toUtc, ct));

    public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        SumDeleteAsync(Stream.Trades, s => s.DeleteTradesInRangeAsync(fromUtc, toUtc, ct));

    public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        SumDeleteAsync(Stream.Bars, s => s.DeleteBarsInRangeAsync(fromUtc, toUtc, ct));

    public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        SumDeleteAsync(Stream.Depth, s => s.DeleteDepthInRangeAsync(fromUtc, toUtc, ct));

    private async Task<long> SumDeleteAsync(Stream stream, Func<SqliteMarketDataStore, Task<long>> delete)
    {
        long total = 0;
        foreach (var deleted in await Task.WhenAll(Open(stream).Select(delete)).ConfigureAwait(false))
            total += deleted;
        return total;
    }

    // ── Per-(broker, stream) file management ────────────────────────────────────────────────────
    private SqliteMarketDataStore StoreFor(BrokerKind broker, Stream stream) =>
        _stores.GetOrAdd((broker, stream), key => new Lazy<SqliteMarketDataStore>(() => Create(key.Broker, key.Stream))).Value;

    /// <summary>The store for a (broker, stream) only if its file already exists (or is already open) —
    /// never spins up a writer for one that has never written, so a source-scoped read of an unknown
    /// broker is just "empty".</summary>
    private SqliteMarketDataStore? ExistingStoreFor(BrokerKind broker, Stream stream)
    {
        if (_stores.TryGetValue((broker, stream), out var lazy)) return lazy.Value;
        return File.Exists(PathFor(broker, stream)) ? StoreFor(broker, stream) : null;
    }

    private SqliteMarketDataStore Create(BrokerKind broker, Stream stream)
    {
        var conn = new SqliteConnectionStringBuilder { DataSource = PathFor(broker, stream) }.ToString();
        return new SqliteMarketDataStore(
            conn, _persist, _batchSize, _loggerFactory.CreateLogger<SqliteMarketDataStore>(),
            SchemaStream(stream), stream == Stream.Depth ? _depthRetentionDays : 0);
    }

    private IEnumerable<SqliteMarketDataStore> All() => _stores.Values.Select(l => l.Value);
    private IEnumerable<SqliteMarketDataStore> Open(Stream stream) =>
        _stores.Where(kv => kv.Key.Stream == stream).Select(kv => kv.Value.Value);

    private string PathFor(BrokerKind broker, Stream stream) =>
        Path.Combine(_baseDirectory, $"{_fileStem}-{broker}-{Suffix(stream)}.db");

    private static string Suffix(Stream s) => s switch
    {
        Stream.Quotes => "l1",
        Stream.Trades => "trades",
        Stream.Bars   => "bars",
        Stream.Depth  => "l2",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static Stream? StreamFromSuffix(string suffix) => suffix switch
    {
        "l1"     => Stream.Quotes,
        "trades" => Stream.Trades,
        "bars"   => Stream.Bars,
        "l2"     => Stream.Depth,
        _ => null,
    };

    private static SqliteStoreStream SchemaStream(Stream s) => s switch
    {
        Stream.Quotes => SqliteStoreStream.Quotes,
        Stream.Trades => SqliteStoreStream.Trades,
        Stream.Bars   => SqliteStoreStream.Bars,
        Stream.Depth  => SqliteStoreStream.Depth,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private IEnumerable<(BrokerKind Broker, Stream Stream)> DiscoverExistingFiles()
    {
        // {stem}-{broker}-{suffix}.db — two trailing segments. (Skips any legacy {stem}-{broker}.db.)
        foreach (var path in Directory.EnumerateFiles(_baseDirectory, $"{_fileStem}-*-*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(path);     // {stem}-{broker}-{suffix}
            var body = name[(_fileStem.Length + 1)..];             // {broker}-{suffix}
            var dash = body.LastIndexOf('-');
            if (dash <= 0) continue;
            if (Enum.TryParse<BrokerKind>(body[..dash], out var broker) && StreamFromSuffix(body[(dash + 1)..]) is { } stream)
                yield return (broker, stream);
        }
    }

    // ── Merge helpers ──────────────────────────────────────────────────────────────────────────
    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <summary>k-way merge of per-broker streams (each already ascending) into one globally-ascending
    /// stream by <paramref name="key"/>. k = number of broker files, so the linear head scan is cheap.</summary>
    private static async IAsyncEnumerable<T> MergeAscending<T>(
        IEnumerable<IAsyncEnumerable<T>> sources, Func<T, DateTime> key,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var heads = new List<IAsyncEnumerator<T>>();
        try
        {
            foreach (var src in sources)
            {
                var e = src.GetAsyncEnumerator(ct);
                if (await e.MoveNextAsync().ConfigureAwait(false)) heads.Add(e);
                else await e.DisposeAsync().ConfigureAwait(false);
            }

            while (heads.Count > 0)
            {
                var min = 0;
                for (var i = 1; i < heads.Count; i++)
                    if (key(heads[i].Current) < key(heads[min].Current)) min = i;

                var picked = heads[min];
                yield return picked.Current;

                if (!await picked.MoveNextAsync().ConfigureAwait(false))
                {
                    await picked.DisposeAsync().ConfigureAwait(false);
                    heads.RemoveAt(min);
                }
            }
        }
        finally
        {
            foreach (var e in heads) await e.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        // Each inner Dispose joins its writer (≤3s); do them in parallel so many files don't serialize.
        var stores = _stores.Values.Where(l => l.IsValueCreated).Select(l => l.Value).ToArray();
        try { Task.WaitAll(stores.Select(s => Task.Run(s.Dispose)).ToArray(), TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }
        _stores.Clear();
    }
}
