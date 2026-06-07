using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Parquet.Serialization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>
/// Exports a date-range slice of the store into one self-describing parquet <em>document per
/// (instrument, data-type, broker[, bar-size])</em>. Each document is named so the file alone tells
/// you what it holds — e.g. <c>AAPL_NASDAQ_IB_L1_20260601-20260607.parquet</c> — and the archiver
/// uploads each as its own Telegram document, splitting only the individual files that exceed the
/// size cap. No outer zip and no inner manifest: every document carries its identity in the manifest
/// entry's blob metadata, so the restore path reassembles and re-imports each one independently.
/// </summary>
internal sealed class ArchiveBundleBuilder
{
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
        string stagingDir, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        var workDir = Path.Combine(stagingDir, $"build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var docs = new List<BuiltDocument>();
        var instruments = _registry.All();
        progress?.Report($"Exporting {instruments.Count} instrument(s) for [{fromUtc:yyyy-MM-dd} → {toUtc:yyyy-MM-dd})…");

        long rowsQuotes = 0, rowsBars = 0, rowsTrades = 0, rowsDepth = 0;
        if (tables.HasFlag(ArchiveTables.Quotes))
            rowsQuotes = await ExportQuotesAsync(instruments, fromUtc, toUtc, workDir, docs, ct);
        if (tables.HasFlag(ArchiveTables.Bars))
            rowsBars = await ExportBarsAsync(instruments, fromUtc, toUtc, workDir, docs, ct);
        if (tables.HasFlag(ArchiveTables.Trades))
            rowsTrades = await ExportTradesAsync(instruments, fromUtc, toUtc, workDir, docs, ct);
        if (tables.HasFlag(ArchiveTables.Depth))
            rowsDepth = await ExportDepthAsync(instruments, fromUtc, toUtc, workDir, docs, ct);

        var totalBytes = docs.Sum(d => d.SizeBytes);
        progress?.Report(
            $"Exported {rowsQuotes:n0} quotes, {rowsBars:n0} bars, {rowsTrades:n0} trades, " +
            $"{rowsDepth:n0} depth rows across {docs.Count} document(s) ({Fmt(totalBytes)}).");

        return new BundleResult(docs, rowsQuotes, rowsBars, rowsTrades, rowsDepth, totalBytes, workDir);
    }

    private async Task<long> ExportQuotesAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string workDir, List<BuiltDocument> docs, CancellationToken ct)
    {
        long total = 0;
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            var bySource = new Dictionary<int, List<QuoteParquetRow>>();
            await foreach (var q in _store.ReadQuotesAsync(ins.Id, fromUtc, toUtc, ct))
            {
                Rows(bySource, q.Source).Add(new QuoteParquetRow
                {
                    InstrumentId = q.InstrumentId.Value,
                    EventTimeMicros = ToMicros(q.EventTimeUtc),
                    IngestTimeMicros = ToMicros(q.IngestTimeUtc),
                    Bid = q.Bid, Ask = q.Ask, BidSize = q.BidSize, AskSize = q.AskSize,
                    Source = (int)q.Source, Sequence = q.Sequence,
                    EventTimeApproximate = q.EventTimeApproximate,
                });
            }
            foreach (var (source, rows) in bySource)
            {
                docs.Add(await WriteDocAsync(
                    rows, "quotes", "L1", ins, (BrokerKind)source, barSizeLabel: "", fromUtc, toUtc, workDir, ct));
                total += rows.Count;
            }
        }
        return total;
    }

    private async Task<long> ExportTradesAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string workDir, List<BuiltDocument> docs, CancellationToken ct)
    {
        long total = 0;
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            var bySource = new Dictionary<int, List<TradeParquetRow>>();
            await foreach (var t in _store.ReadTradesAsync(ins.Id, fromUtc, toUtc, ct))
            {
                Rows(bySource, t.Source).Add(new TradeParquetRow
                {
                    InstrumentId = t.InstrumentId.Value,
                    EventTimeMicros = ToMicros(t.EventTimeUtc),
                    IngestTimeMicros = ToMicros(t.IngestTimeUtc),
                    Price = t.Price, Size = t.Size, Aggressor = (int)t.Aggressor,
                    Source = (int)t.Source, Sequence = t.Sequence,
                    EventTimeApproximate = t.EventTimeApproximate,
                });
            }
            foreach (var (source, rows) in bySource)
            {
                docs.Add(await WriteDocAsync(
                    rows, "trades", "TRADES", ins, (BrokerKind)source, barSizeLabel: "", fromUtc, toUtc, workDir, ct));
                total += rows.Count;
            }
        }
        return total;
    }

    private async Task<long> ExportBarsAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string workDir, List<BuiltDocument> docs, CancellationToken ct)
    {
        long total = 0;
        foreach (var ins in instruments)
        {
            foreach (var size in Enum.GetValues<BarSize>())
            {
                ct.ThrowIfCancellationRequested();
                var bySource = new Dictionary<int, List<BarParquetRow>>();
                await foreach (var b in _store.ReadBarsAsync(ins.Id, size, fromUtc, toUtc, ct))
                {
                    Rows(bySource, b.Source).Add(new BarParquetRow
                    {
                        InstrumentId = b.InstrumentId.Value,
                        BarSize = (int)b.Size,
                        OpenTimeMicros = ToMicros(b.OpenTimeUtc),
                        Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                        Volume = b.Volume, Source = (int)b.Source, IsFinal = b.IsFinal,
                    });
                }
                var sizeLabel = size.ToDisplayString();
                foreach (var (source, rows) in bySource)
                {
                    docs.Add(await WriteDocAsync(
                        rows, "bars", "BAR" + sizeLabel, ins, (BrokerKind)source, sizeLabel, fromUtc, toUtc, workDir, ct));
                    total += rows.Count;
                }
            }
        }
        return total;
    }

    private async Task<long> ExportDepthAsync(
        IReadOnlyList<Instrument> instruments, DateTime fromUtc, DateTime toUtc,
        string workDir, List<BuiltDocument> docs, CancellationToken ct)
    {
        long total = 0;
        foreach (var ins in instruments)
        {
            ct.ThrowIfCancellationRequested();
            // Depth read-back doesn't surface the originating broker (it's a write-side column), so
            // depth documents carry no broker token — book structure + event time is what matters.
            var rows = new List<DepthParquetRow>();
            await foreach (var s in _store.ReadDepthAsync(ins.Id, fromUtc, toUtc, ct))
            {
                var eventMicros = ToMicros(s.TimestampUtc);
                AppendLevels(rows, ins.Id.Value, eventMicros, "B", s.Bids);
                AppendLevels(rows, ins.Id.Value, eventMicros, "A", s.Asks);
            }
            if (rows.Count == 0) continue;
            docs.Add(await WriteDocAsync(
                rows, "depth", "L2", ins, broker: null, barSizeLabel: "", fromUtc, toUtc, workDir, ct));
            total += rows.Count;
        }
        return total;
    }

    /// <summary>Serialize one logical document's rows to a descriptively-named parquet and capture its
    /// size + sha256 so the archiver can upload, split, and verify it.</summary>
    private async Task<BuiltDocument> WriteDocAsync<T>(
        IReadOnlyList<T> rows, string kind, string typeToken, Instrument ins,
        BrokerKind? broker, string barSizeLabel, DateTime fromUtc, DateTime toUtc,
        string workDir, CancellationToken ct) where T : new()
    {
        var brokerCode = broker is { } b ? BrokerCode(b) : "";
        var name = BuildDocName(ins.CanonicalSymbol, ins.Exchange, brokerCode, typeToken, fromUtc, toUtc);
        var path = UniquePath(workDir, name);
        await WriteParquetAsync(rows, path, ct);
        var size = new FileInfo(path).Length;
        var sha = await ComputeSha256Async(path, ct);
        return new BuiltDocument(
            LocalPath: path,
            DisplayName: Path.GetFileName(path),
            Kind: kind,
            InstrumentId: ins.Id.Value,
            Symbol: ins.CanonicalSymbol,
            Exchange: ins.Exchange,
            Broker: brokerCode,
            BarSizeLabel: barSizeLabel,
            Rows: rows.Count,
            SizeBytes: size,
            Sha256Hex: sha);
    }

    private static List<TRow> Rows<TRow>(Dictionary<int, List<TRow>> bySource, BrokerKind source)
    {
        var key = (int)source;
        if (!bySource.TryGetValue(key, out var list)) bySource[key] = list = new List<TRow>();
        return list;
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

    /// <summary>
    /// <c>{SYMBOL}_{EXCHANGE}_{BROKER}_{TYPE}_{from}-{to}.parquet</c> — empty segments dropped.
    /// TYPE is L1 (quotes), L2 (depth), TRADES, or BAR&lt;size&gt; (e.g. BAR1m).
    /// </summary>
    private static string BuildDocName(
        string symbol, string exchange, string broker, string typeToken, DateTime fromUtc, DateTime toUtc)
    {
        var sb = new StringBuilder();
        sb.Append(Sanitize(symbol));
        if (!string.IsNullOrWhiteSpace(exchange)) sb.Append('_').Append(Sanitize(exchange));
        if (!string.IsNullOrWhiteSpace(broker)) sb.Append('_').Append(Sanitize(broker));
        sb.Append('_').Append(Sanitize(typeToken));
        sb.Append('_').Append($"{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}");
        sb.Append(".parquet");
        return sb.ToString();
    }

    private static string BrokerCode(BrokerKind kind) => kind switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NT",
        BrokerKind.CTrader => "CT",
        BrokerKind.Alpaca => "ALP",
        BrokerKind.Simulated => "SIM",
        _ => kind.ToString(),
    };

    /// <summary>Strip anything that isn't filename-safe so symbols/exchanges with slashes or spaces
    /// (e.g. forex "EUR/USD") don't break the path.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-');
        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    /// <summary>Guard the (rare) case where two logical documents would resolve to the same filename
    /// by appending a numeric suffix — keeps a build self-consistent without surprising overwrites.</summary>
    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static async Task WriteParquetAsync<T>(IReadOnlyCollection<T> rows, string path, CancellationToken ct)
        where T : new()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(path);
        await ParquetSerializer.SerializeAsync(rows, fs, null, ct);
    }

    /// <summary>Binary-split one file into ≤<paramref name="maxPartBytes"/> slices, each sha256'd, so
    /// a single oversized document can still cross the transport's per-upload cap. Slices concatenate
    /// back into the original file byte-for-byte on restore.</summary>
    public static async Task<List<BundlePart>> SplitFileAsync(
        string filePath, string workDir, long maxPartBytes, CancellationToken ct)
    {
        var parts = new List<BundlePart>();
        const int chunk = 4 * 1024 * 1024;
        var buf = new byte[chunk];
        var baseName = Path.GetFileName(filePath);

        await using (var src = File.OpenRead(filePath))
        {
            var partIdx = 1;
            // Pre-compute the part count so names can read "part01of07".
            var partCount = (int)((src.Length + maxPartBytes - 1) / maxPartBytes);
            while (src.Position < src.Length)
            {
                var partName = $"{baseName}.part{partIdx:D2}of{partCount:D2}";
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
        }
        File.Delete(filePath); // keep only the slices
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

/// <summary>One built, descriptively-named parquet document — the unit the archiver uploads (and
/// splits if it alone exceeds the size cap).</summary>
internal sealed record BuiltDocument(
    string LocalPath,
    string DisplayName,
    string Kind,
    int InstrumentId,
    string Symbol,
    string Exchange,
    string Broker,
    string BarSizeLabel,
    long Rows,
    long SizeBytes,
    string Sha256Hex);

/// <summary>One binary slice of an oversized document.</summary>
internal sealed record BundlePart(string LocalPath, string Name, long SizeBytes, string Sha256Hex);

internal sealed record BundleResult(
    IReadOnlyList<BuiltDocument> Documents,
    long RowsQuotes, long RowsBars, long RowsTrades, long RowsDepth,
    long TotalBytes,
    string WorkDir);
