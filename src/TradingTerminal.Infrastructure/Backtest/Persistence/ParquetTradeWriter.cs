using System.IO;
using Parquet.Serialization;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Buffered writer for the optional backtest trade tape (<see cref="TradeRecord"/> schema), mirroring
/// <see cref="ParquetTickWriter"/>. The Python harness writes these directly via pyarrow; this exists
/// for C#-side generation and round-trip tests. Open one per dataset; disposal flushes the tail.
/// </summary>
public sealed class ParquetTradeWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly int _rowGroupSize;
    private readonly List<TradeRecord> _buffer;
    private bool _hasWrittenAny;

    public ParquetTradeWriter(string path, int rowGroupSize = 50_000)
    {
        if (rowGroupSize <= 0) throw new ArgumentOutOfRangeException(nameof(rowGroupSize));
        _path = path;
        _rowGroupSize = rowGroupSize;
        _buffer = new List<TradeRecord>(rowGroupSize);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public async ValueTask WriteAsync(TradePrint trade, CancellationToken ct = default)
    {
        _buffer.Add(new TradeRecord
        {
            TimestampMicros = ToEpochMicros(EnsureUtc(trade.EventTimeUtc)),
            Price = trade.Price,
            Size = trade.Size,
            Aggressor = (int)trade.Aggressor,
        });

        if (_buffer.Count >= _rowGroupSize)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_buffer.Count == 0) return;

        await using var stream = new FileStream(
            _path, _hasWrittenAny ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        await ParquetSerializer.SerializeAsync(
            _buffer, stream, new ParquetSerializerOptions { Append = _hasWrittenAny }, ct).ConfigureAwait(false);

        _hasWrittenAny = true;
        _buffer.Clear();
    }

    public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);

    private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => dt.ToUniversalTime(),
    };

    private static long ToEpochMicros(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks / 10L;
}
