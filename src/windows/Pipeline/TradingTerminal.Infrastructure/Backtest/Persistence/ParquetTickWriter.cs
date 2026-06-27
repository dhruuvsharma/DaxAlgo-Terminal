using System.IO;
using Parquet.Serialization;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Buffered writer that accumulates ticks into row groups (default 50,000 rows per group)
/// then appends them to a parquet file. Single-writer, single-file — open one per dataset.
/// Disposal flushes any partial buffer; failing to dispose leaves the tail of your data
/// unwritten.
/// </summary>
public sealed class ParquetTickWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly int _rowGroupSize;
    private readonly List<TickRecord> _buffer;
    private bool _hasWrittenAny;

    public ParquetTickWriter(string path, int rowGroupSize = 50_000)
    {
        if (rowGroupSize <= 0) throw new ArgumentOutOfRangeException(nameof(rowGroupSize));
        _path = path;
        _rowGroupSize = rowGroupSize;
        _buffer = new List<TickRecord>(rowGroupSize);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public async ValueTask WriteAsync(Tick tick, CancellationToken ct = default)
    {
        _buffer.Add(new TickRecord
        {
            TimestampMicros = ToEpochMicros(EnsureUtc(tick.TimestampUtc)),
            Bid = tick.Bid,
            Ask = tick.Ask,
            BidSize = tick.BidSize,
            AskSize = tick.AskSize,
        });

        if (_buffer.Count >= _rowGroupSize)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_buffer.Count == 0) return;

        await using var stream = new FileStream(
            _path,
            _hasWrittenAny ? FileMode.Open : FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);

        await ParquetSerializer.SerializeAsync(
            _buffer,
            stream,
            new ParquetSerializerOptions { Append = _hasWrittenAny },
            ct).ConfigureAwait(false);

        _hasWrittenAny = true;
        _buffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
    }

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime utc) =>
        (utc - DateTime.UnixEpoch).Ticks / 10L;
}
