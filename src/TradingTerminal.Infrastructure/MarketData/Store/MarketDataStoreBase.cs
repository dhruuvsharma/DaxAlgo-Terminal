using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    protected enum WriteKind { Quote, Trade, Bar }

    protected readonly record struct WriteOp(WriteKind Kind, Quote? Quote, TradePrint? Trade, OhlcvBar? Bar);

    private readonly bool _persist;
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
        InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default);

    public abstract IAsyncEnumerable<Quote> ReadQuotesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    public abstract IAsyncEnumerable<TradePrint> ReadTradesAsync(
        InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

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
