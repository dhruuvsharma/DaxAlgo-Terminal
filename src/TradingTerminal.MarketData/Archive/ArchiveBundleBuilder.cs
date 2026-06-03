using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>
/// Exports a date-range slice of the store into a multi-parquet zip bundle, then binary-splits
/// the zip if it exceeds <see cref="ArchiveOptions.MaxPartBytes"/>. Cohesive with the canonical
/// store schema — one parquet file per (instrument, table) — so the restore path can stream each
/// file straight back into <c>Enqueue*</c>. The bundle's <c>manifest.json</c> lists what's inside
/// so the restorer doesn't have to enumerate parquets blindly.
/// </summary>
internal sealed class ArchiveBundleBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly ILogger _logger;

    public ArchiveBundleBuilder(IMarketDataStore store, IInstrumentRegistry registry, ILogger logger)
    {
        _store = store;
        _registry = registry;
        _logger = logger;
    }

    public async Task<BundleResult> BuildAsync(
        DateTime fromUtc, DateTime toUtc, ArchiveTables tables,
        string stagingDir, long maxPartBytes,
        IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        var workDir = Path.Combine(stagingDir, $"build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var parquetDir = Path.Combine(workDir, "parquet");
        Directory.CreateDirectory(parquetDir);

        var bundleManifest = new BundleManifest { FromUtc = fromUtc, ToUtc = toUtc };
        var instruments = _registry.All();

        progress?.Report($"Exporting {instruments.Count} instruments…");

        if (tables.HasFlag(ArchiveTables.Quotes))
            await ExportQuotesAsync(instruments, fromUtc, toUtc, parquetDir, bundleManifest, ct);
        if (tables.HasFlag(ArchiveTables.Bars))
            await ExportBarsAsync(instruments, fromUtc, toUtc, parquetDir, bundleManifest, ct);
        if (tables.HasFlag(ArchiveTables.Trades))
            await ExportTradesAsync(instruments, fromUtc, toUtc, parquetDir, bundleManifest, ct);
        if (tables.HasFlag(ArchiveTables.Depth))
            await ExportDepthAsync(instruments, fromUtc, toUtc, parquetDir, bundleManifest, ct);

        progress?.Report(
            $"Exported {bundleManifest.RowsQuotes:n0} quotes, {bundleManifest.RowsBars:n0} bars, " +
            $"{bundleManifest.RowsTrades:n0} trades, {bundleManifest.RowsDepth:n0} depth rows " +
            $"across {bundleManifest.Files.Count} parquet files.");

        // Write manifest.json alongside the parquets so it ends up in the zip too.
        var manifestPath = Path.Combine(parquetDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(bundleManifest, JsonOpts), ct);

        // Pack into a single zip first (parquets are already compressed → store-level entries).
        var zipPath = Path.Combine(workDir, "bundle.zip");
        await using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var f in Directory.EnumerateFiles(parquetDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(parquetDir, f).Replace('\\', '/');
                var compression = f.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression  // parquet is already zstd-compressed
                    : CompressionLevel.Optimal;
                var entry = zip.CreateEntry(rel, compression);
                await using var es = entry.Open();
                await using var src = File.OpenRead(f);
                await src.CopyToAsync(es, ct);
            }
        }
        Directory.Delete(parquetDir, recursive: true);

        var zipSize = new FileInfo(zipPath).Length;
        progress?.Report($"Bundle assembled: {Fmt(zipSize)}.");

        // Split into parts if any single upload would exceed the cap.
        var parts = zipSize <= maxPartBytes
            ? new List<BundlePart> { new(zipPath, Path.GetFileName(zipPath), zipSize, await ComputeSha256Async(zipPath, ct)) }
            : await SplitAsync(zipPath, workDir, maxPartBytes, ct);

        if (parts.Count > 1)
            progress?.Report($"Split into {parts.Count} parts (cap = {Fmt(maxPartBytes)}).");

        return new BundleResult(parts, bundleManifest.RowsQuotes, bundleManifest.RowsBars,
            bundleManifest.RowsTrades, bundleManifest.RowsDepth, zipSize, workDir);
    }

    private async Task ExportQuotesAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string outDir, BundleManifest manifest, CancellationToken ct)
    {
        var dir = Path.Combine(outDir, "quotes");
        Directory.CreateDirectory(dir);
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<QuoteParquetRow>();
            await foreach (var q in _store.ReadQuotesAsync(ins.Id, fromUtc, toUtc, ct))
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
            if (rows.Count == 0) continue;
            var rel = $"quotes/instrument_{ins.Id.Value}.parquet";
            var abs = Path.Combine(outDir, rel);
            await WriteParquetAsync(rows, abs, ct);
            manifest.Files.Add(new BundleFile { Path = rel, Kind = "quotes", InstrumentId = ins.Id.Value, Rows = rows.Count });
            manifest.RowsQuotes += rows.Count;
        }
    }

    private async Task ExportBarsAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string outDir, BundleManifest manifest, CancellationToken ct)
    {
        var dir = Path.Combine(outDir, "bars");
        Directory.CreateDirectory(dir);
        foreach (var ins in instruments)
        {
            foreach (var size in Enum.GetValues<BarSize>())
            {
                ct.ThrowIfCancellationRequested();
                var rows = new List<BarParquetRow>();
                await foreach (var b in _store.ReadBarsAsync(ins.Id, size, fromUtc, toUtc, ct))
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
                if (rows.Count == 0) continue;
                var rel = $"bars/instrument_{ins.Id.Value}_size{(int)size}.parquet";
                var abs = Path.Combine(outDir, rel);
                await WriteParquetAsync(rows, abs, ct);
                manifest.Files.Add(new BundleFile
                {
                    Path = rel, Kind = "bars", InstrumentId = ins.Id.Value,
                    BarSize = (int)size, Rows = rows.Count,
                });
                manifest.RowsBars += rows.Count;
            }
        }
    }

    private async Task ExportTradesAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string outDir, BundleManifest manifest, CancellationToken ct)
    {
        var dir = Path.Combine(outDir, "trades");
        Directory.CreateDirectory(dir);
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<TradeParquetRow>();
            await foreach (var t in _store.ReadTradesAsync(ins.Id, fromUtc, toUtc, ct))
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
            if (rows.Count == 0) continue;
            var rel = $"trades/instrument_{ins.Id.Value}.parquet";
            var abs = Path.Combine(outDir, rel);
            await WriteParquetAsync(rows, abs, ct);
            manifest.Files.Add(new BundleFile { Path = rel, Kind = "trades", InstrumentId = ins.Id.Value, Rows = rows.Count });
            manifest.RowsTrades += rows.Count;
        }
    }

    private async Task ExportDepthAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string outDir, BundleManifest manifest, CancellationToken ct)
    {
        var dir = Path.Combine(outDir, "depth");
        Directory.CreateDirectory(dir);
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            var rows = new List<DepthParquetRow>();
            await foreach (var s in _store.ReadDepthAsync(ins.Id, fromUtc, toUtc, ct))
            {
                // Flatten the snapshot to one row per level (the on-disk shape). The canonical
                // DepthSnapshot carries no source/ingest-time in-band — those are write-side columns
                // ReadDepthAsync doesn't surface — so the archive preserves book structure + event
                // time, and restore stamps Source=Unknown / ingest=event time. Faithful for L2 research.
                var eventMicros = ToMicros(s.TimestampUtc);
                AppendLevels(rows, ins.Id.Value, eventMicros, "B", s.Bids);
                AppendLevels(rows, ins.Id.Value, eventMicros, "A", s.Asks);
            }
            if (rows.Count == 0) continue;
            var rel = $"depth/instrument_{ins.Id.Value}.parquet";
            var abs = Path.Combine(outDir, rel);
            await WriteParquetAsync(rows, abs, ct);
            manifest.Files.Add(new BundleFile { Path = rel, Kind = "depth", InstrumentId = ins.Id.Value, Rows = rows.Count });
            manifest.RowsDepth += rows.Count;
        }
    }

    private static void AppendLevels(
        List<DepthParquetRow> rows, long instrumentId, long eventMicros,
        string side, IReadOnlyList<DepthLevel> levels)
    {
        for (var i = 0; i < levels.Count; i++)
            rows.Add(new DepthParquetRow
            {
                InstrumentId = instrumentId,
                EventTimeMicros = eventMicros,
                IngestTimeMicros = eventMicros, // ingest time not surfaced by ReadDepthAsync
                Side = side, Level = i,
                Price = levels[i].Price, Size = levels[i].Size,
                Source = 0, // BrokerKind not surfaced by ReadDepthAsync; restore stamps Unknown
            });
    }

    private static async Task WriteParquetAsync<T>(IReadOnlyCollection<T> rows, string path, CancellationToken ct)
        where T : new()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null, ct);
    }

    private static async Task<List<BundlePart>> SplitAsync(
        string zipPath, string workDir, long maxPartBytes, CancellationToken ct)
    {
        var parts = new List<BundlePart>();
        const int chunk = 4 * 1024 * 1024;
        var buf = new byte[chunk];

        await using var src = File.OpenRead(zipPath);
        var partIdx = 1;
        while (src.Position < src.Length)
        {
            var partName = $"bundle.zip.part{partIdx:D2}";
            var partPath = Path.Combine(workDir, partName);
            using var sha = SHA256.Create();
            long written = 0;
            await using (var dst = File.Create(partPath))
            {
                while (written < maxPartBytes && src.Position < src.Length)
                {
                    int toRead = (int)Math.Min(chunk, maxPartBytes - written);
                    int n = await src.ReadAsync(buf.AsMemory(0, toRead), ct);
                    if (n == 0) break;
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    written += n;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            parts.Add(new BundlePart(partPath, partName, written, Convert.ToHexString(sha.Hash!).ToLowerInvariant()));
            partIdx++;
        }
        await src.DisposeAsync();
        File.Delete(zipPath); // keep only the parts
        return parts;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static long ToMicros(DateTime utc) =>
        ((utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()) - DateTime.UnixEpoch).Ticks / 10L;

    private static string Fmt(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
}

internal sealed record BundlePart(string LocalPath, string Name, long SizeBytes, string Sha256Hex);

internal sealed record BundleResult(
    IReadOnlyList<BundlePart> Parts,
    long RowsQuotes, long RowsBars, long RowsTrades, long RowsDepth,
    long TotalUncompressedBytes,
    string WorkDir);
