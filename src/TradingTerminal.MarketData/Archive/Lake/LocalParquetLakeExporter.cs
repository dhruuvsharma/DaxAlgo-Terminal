using System.IO;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive.Lake;

/// <summary>
/// Writes a closed period of the canonical store to a persistent, DuckDB-friendly Parquet tree
/// on the local disk. Reuses the same per-instrument Parquet row schema as the Telegram offloader
/// (<see cref="QuoteParquetRow"/> / <see cref="BarParquetRow"/> / <see cref="TradeParquetRow"/>,
/// epoch-micros timestamps + full provenance) so files are interchangeable across both paths.
///
/// Layout (one file per instrument-table-period, glob-friendly for <c>read_parquet(...)</c>):
/// <code>
///   &lt;root&gt;/quotes/instrument=&lt;id&gt;/&lt;period&gt;.parquet
///   &lt;root&gt;/trades/instrument=&lt;id&gt;/&lt;period&gt;.parquet
///   &lt;root&gt;/bars/instrument=&lt;id&gt;/size=&lt;n&gt;/&lt;period&gt;.parquet
/// </code>
///
/// Append-only and idempotent: a target file that already exists is left untouched (never
/// modified), so re-running the same period is a safe no-op — matching the "Parquet export is
/// append-only" rule. Empty (instrument, table, period) slices write no file.
/// </summary>
public sealed class LocalParquetLakeExporter
{
    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly ILogger<LocalParquetLakeExporter> _logger;

    public LocalParquetLakeExporter(
        IMarketDataStore store,
        IInstrumentRegistry registry,
        ILogger<LocalParquetLakeExporter> logger)
    {
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ParquetLakeExportResult> ExportRangeAsync(
        DateTime fromUtc, DateTime toUtc, string rootDir, string periodLabel,
        ArchiveTables tables, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(rootDir);
        var instruments = _registry.All();
        var result = new ParquetLakeExportResult();
        progress?.Report($"Lake export [{fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd}) for {instruments.Count} instruments…");

        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();

            if (tables.HasFlag(ArchiveTables.Quotes))
                await ExportQuotesAsync(ins, fromUtc, toUtc, rootDir, periodLabel, result, ct).ConfigureAwait(false);
            if (tables.HasFlag(ArchiveTables.Trades))
                await ExportTradesAsync(ins, fromUtc, toUtc, rootDir, periodLabel, result, ct).ConfigureAwait(false);
            if (tables.HasFlag(ArchiveTables.Bars))
                await ExportBarsAsync(ins, fromUtc, toUtc, rootDir, periodLabel, result, ct).ConfigureAwait(false);
        }

        progress?.Report(
            $"Lake export done: {result.FilesWritten} files written, {result.FilesSkipped} skipped " +
            $"({result.Rows:n0} rows).");
        return result;
    }

    private async Task ExportQuotesAsync(
        Instrument ins, DateTime fromUtc, DateTime toUtc, string rootDir, string label,
        ParquetLakeExportResult result, CancellationToken ct)
    {
        var path = Path.Combine(rootDir, "quotes", $"instrument={ins.Id.Value}", $"{label}.parquet");
        if (Skip(path, result)) return;

        var rows = new List<QuoteParquetRow>();
        await foreach (var q in _store.ReadQuotesAsync(ins.Id, fromUtc, toUtc, ct).ConfigureAwait(false))
        {
            rows.Add(new QuoteParquetRow
            {
                InstrumentId = q.InstrumentId.Value,
                EventTimeMicros = ToMicros(q.EventTimeUtc),
                IngestTimeMicros = ToMicros(q.IngestTimeUtc),
                Bid = q.Bid, Ask = q.Ask, BidSize = q.BidSize, AskSize = q.AskSize,
                Source = (int)q.Source, Sequence = q.Sequence,
                EventTimeApproximate = q.EventTimeApproximate,
            });
        }
        await WriteIfAnyAsync(rows, path, result, ct).ConfigureAwait(false);
    }

    private async Task ExportTradesAsync(
        Instrument ins, DateTime fromUtc, DateTime toUtc, string rootDir, string label,
        ParquetLakeExportResult result, CancellationToken ct)
    {
        var path = Path.Combine(rootDir, "trades", $"instrument={ins.Id.Value}", $"{label}.parquet");
        if (Skip(path, result)) return;

        var rows = new List<TradeParquetRow>();
        await foreach (var t in _store.ReadTradesAsync(ins.Id, fromUtc, toUtc, ct).ConfigureAwait(false))
        {
            rows.Add(new TradeParquetRow
            {
                InstrumentId = t.InstrumentId.Value,
                EventTimeMicros = ToMicros(t.EventTimeUtc),
                IngestTimeMicros = ToMicros(t.IngestTimeUtc),
                Price = t.Price, Size = t.Size, Aggressor = (int)t.Aggressor,
                Source = (int)t.Source, Sequence = t.Sequence,
                EventTimeApproximate = t.EventTimeApproximate,
            });
        }
        await WriteIfAnyAsync(rows, path, result, ct).ConfigureAwait(false);
    }

    private async Task ExportBarsAsync(
        Instrument ins, DateTime fromUtc, DateTime toUtc, string rootDir, string label,
        ParquetLakeExportResult result, CancellationToken ct)
    {
        foreach (var size in Enum.GetValues<BarSize>())
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(rootDir, "bars", $"instrument={ins.Id.Value}", $"size={(int)size}", $"{label}.parquet");
            if (Skip(path, result)) continue;

            var rows = new List<BarParquetRow>();
            await foreach (var b in _store.ReadBarsAsync(ins.Id, size, fromUtc, toUtc, ct).ConfigureAwait(false))
            {
                rows.Add(new BarParquetRow
                {
                    InstrumentId = b.InstrumentId.Value,
                    BarSize = (int)b.Size,
                    OpenTimeMicros = ToMicros(b.OpenTimeUtc),
                    Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                    Volume = b.Volume, Source = (int)b.Source, IsFinal = b.IsFinal,
                });
            }
            await WriteIfAnyAsync(rows, path, result, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True (and bumps the skip counter) when the target already exists — append-only,
    /// never overwrite.</summary>
    private bool Skip(string path, ParquetLakeExportResult result)
    {
        if (!File.Exists(path)) return false;
        result.FilesSkipped++;
        return true;
    }

    private async Task WriteIfAnyAsync<T>(
        IReadOnlyCollection<T> rows, string path, ParquetLakeExportResult result, CancellationToken ct)
        where T : new()
    {
        if (rows.Count == 0) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null, ct).ConfigureAwait(false);
        result.FilesWritten++;
        result.Rows += rows.Count;
        _logger.LogDebug("Lake wrote {Rows} rows → {Path}", rows.Count, path);
    }

    private static long ToMicros(DateTime utc) =>
        ((utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()) - DateTime.UnixEpoch).Ticks / 10L;
}

/// <summary>Tally returned by <see cref="LocalParquetLakeExporter.ExportRangeAsync"/>.</summary>
public sealed class ParquetLakeExportResult
{
    public int FilesWritten { get; set; }
    public int FilesSkipped { get; set; }
    public long Rows { get; set; }
}
