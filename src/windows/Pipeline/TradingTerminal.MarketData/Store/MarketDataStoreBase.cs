using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Shared plumbing for an <see cref="IMarketDataStore"/> that persists via a non-blocking
/// batched background writer: enqueue is a lock-free channel write, and a single drain task
/// groups records into one transaction per cycle. Provider-specific subclasses implement
/// <see cref="WriteBatch"/> and the read queries; this base owns the channel, batching, flush
/// barrier, and lifecycle. Subclasses MUST call <see cref="StartWriter"/> at the end of their
/// constructor (once their connection is ready).
/// </summary>
internal abstract class MarketDataStoreBase : IMarketDataStore, IDisposable
{
    protected enum WriteKind { Quote, Trade, Bar, Depth }

    /// <summary>One persisted L2 snapshot plus the provenance the <see cref="DepthSnapshot"/> record
    /// itself doesn't carry (instrument, source broker, ingest time).</summary>
    protected sealed record DepthRecord(InstrumentId InstrumentId, DepthSnapshot Snapshot, BrokerKind Source, DateTime IngestTimeUtc);

    protected readonly record struct WriteOp(WriteKind Kind, Quote? Quote, TradePrint? Trade, OhlcvBar? Bar, DepthRecord? Depth = null);

    private volatile bool _persist;
    private readonly int _batchSize;
    private readonly ILogger _logger;
    private readonly Channel<object> _channel; // WriteOp records or TaskCompletionSource flush markers
    private readonly CancellationTokenSource _cts = new();
    private Task? _writerLoop;

    protected MarketDataStoreBase(bool persist, int batchSize, ILogger logger)
    {
        _persist = persist;
        _batchSize = Math.Max(1, batchSize);
        _logger = logger;
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(64_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    protected void StartWriter() => _writerLoop = Task.Run(() => RunWriterAsync(_cts.Token));

    /// <summary>Turns persistence on for a store that was constructed inert (e.g. QuestDB unreachable at
    /// startup). Subclasses call this once their backend connection is live so enqueues stop no-opping —
    /// no app restart required. The writer loop is already running.</summary>
    protected void EnablePersistence() => _persist = true;

    public void EnqueueQuote(Quote quote)
    {
        if (_persist) _channel.Writer.TryWrite(new WriteOp(WriteKind.Quote, quote, null, null));
    }

    public void EnqueueTrade(TradePrint trade)
    {
        if (_persist) _channel.Writer.TryWrite(new WriteOp(WriteKind.Trade, null, trade, null));
    }

    public void EnqueueBar(OhlcvBar bar)
    {
        if (_persist) _channel.Writer.TryWrite(new WriteOp(WriteKind.Bar, null, null, bar));
    }

    public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source)
    {
        if (_persist)
            _channel.Writer.TryWrite(new WriteOp(
                WriteKind.Depth, null, null, null,
                new DepthRecord(instrumentId, snapshot, source, DateTime.UtcNow)));
    }

    /// <summary>Default: report "no data". Backends that can cheaply MIN/MAX their time columns
    /// override this so the archive coverage view knows the real data span.</summary>
    public virtual Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) =>
        Task.FromResult(StoredDataExtent.Empty);

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!_persist) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channel.Writer.TryWrite(tcs);
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunWriterAsync(CancellationToken ct)
    {
        var batch = new List<WriteOp>(_batchSize);
        var flushes = new List<TaskCompletionSource>();
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                flushes.Clear();
                while (batch.Count < _batchSize && _channel.Reader.TryRead(out var item))
                {
                    if (item is TaskCompletionSource flush) flushes.Add(flush);
                    else batch.Add((WriteOp)item);
                }

                if (batch.Count > 0)
                {
                    try { WriteBatch(batch); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Market-data batch write failed ({Count} rows)", batch.Count); }
                }

                foreach (var f in flushes) f.TrySetResult();
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Market-data writer loop crashed"); }
        finally
        {
            foreach (var f in flushes) f.TrySetResult();
        }
    }

    /// <summary>Persist one batch in a single transaction. Called only on the writer thread.</summary>
    protected abstract void WriteBatch(IReadOnlyList<WriteOp> batch);

    public abstract Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
        InstrumentId instrumentId, BarSize size, int count, BrokerKind? source = null,
        CancellationToken ct = default);

    public abstract IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        CancellationToken ct = default);

    public abstract IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
        CancellationToken ct = default);

    /// <summary>Default: no depth persistence (SQLite/Postgres). QuestDB overrides this.</summary>
    public virtual async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public abstract IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
        InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
        BrokerKind? source = null, CancellationToken ct = default);

    public abstract Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    public abstract Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    public abstract Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Default: no depth persistence (SQLite/Postgres). QuestDB overrides this.</summary>
    public virtual Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        Task.FromResult(0L);

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        try { _writerLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
        _cts.Cancel();
        _cts.Dispose();
        OnDispose();
    }

    /// <summary>Release provider-specific resources (connections). Called after the writer drains.</summary>
    protected virtual void OnDispose() { }
}
