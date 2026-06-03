using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Serialization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>
/// Default <see cref="IMarketDataArchiver"/>. Stitches together the bundle builder, the configured
/// transport, the manifest store, and the canonical store's delete API.
///
/// Archive flow: build bundle → upload every part → (optionally) re-download + sha256 verify →
/// record manifest entry → (optionally) prune local rows. Anything destructive is gated on the
/// verification step succeeding.
///
/// Restore flow: download every part → reassemble (cat) → unzip → parse manifest.json → for each
/// parquet, deserialize rows and re-enqueue them on the store (relying on store-level
/// upsert/ignore semantics for idempotence).
/// </summary>
internal sealed class MarketDataArchiver : IMarketDataArchiver
{
    private readonly IMarketDataStore _store;
    private readonly IInstrumentRegistry _registry;
    private readonly IArchiveTransport _transport;
    private readonly ArchiveManifestStore _manifest;
    private readonly IOptionsMonitor<ArchiveOptions> _options;
    private readonly ILogger<MarketDataArchiver> _logger;

    public MarketDataArchiver(
        IMarketDataStore store,
        IInstrumentRegistry registry,
        IArchiveTransport transport,
        ArchiveManifestStore manifest,
        IOptionsMonitor<ArchiveOptions> options,
        ILogger<MarketDataArchiver> logger)
    {
        _store = store;
        _registry = registry;
        _transport = transport;
        _manifest = manifest;
        _options = options;
        _logger = logger;
    }

    public async Task<ArchiveResult> ArchiveRangeAsync(
        DateTime fromUtc, DateTime toUtc,
        ArchiveTarget target,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (toUtc <= fromUtc) throw new ArgumentException("toUtc must be after fromUtc.");
        var opts = _options.CurrentValue;

        // Idempotence: same exact range → return the existing entry, do nothing.
        var existing = _manifest.FindOverlapping(fromUtc, toUtc);
        if (existing is not null && existing.Transport == _transport.Name)
        {
            progress?.Report($"Range already archived as #{existing.Id} ({existing.PeriodLabel}).");
            return new ArchiveResult(existing, VerifiedRoundTrip: false, LocalDataDeleted: existing.DeletedLocal);
        }

        if (!_transport.IsReady)
            throw new InvalidOperationException(
                $"Transport '{_transport.Name}' is not ready (credentials missing or login pending).");

        var stagingRoot = opts.StagingDirectory ?? DefaultStaging();
        var builder = new ArchiveBundleBuilder(_store, _registry, _logger);

        progress?.Report($"Building bundle for [{fromUtc:s} → {toUtc:s})…");
        var bundle = await builder.BuildAsync(
            fromUtc, toUtc, opts.Tables, stagingRoot, opts.MaxPartBytes, progress, ct);

        try
        {
            // Upload every part.
            var uploadedRefs = new List<ArchiveBlobRef>();
            var periodLabel = BuildPeriodLabel(fromUtc, toUtc, opts.Period);
            for (var i = 0; i < bundle.Parts.Count; i++)
            {
                var part = bundle.Parts[i];
                var display = bundle.Parts.Count == 1
                    ? $"daxalgo-marketdata-{periodLabel}.zip"
                    : $"daxalgo-marketdata-{periodLabel}.zip.part{i + 1:D2}";
                progress?.Report($"Uploading {display} ({Fmt(part.SizeBytes)})…");
                await using var fs = File.OpenRead(part.LocalPath);
                var blob = await _transport.UploadAsync(fs, display, part.SizeBytes, target,
                    new Progress<long>(b => { /* per-part progress events could route to UI */ }),
                    ct);
                uploadedRefs.Add(blob);
            }

            // Verify round-trip (optional but on by default).
            var verified = false;
            if (opts.VerifyAfterUpload)
            {
                progress?.Report("Verifying upload checksums…");
                for (var i = 0; i < bundle.Parts.Count; i++)
                {
                    var part = bundle.Parts[i];
                    var blob = uploadedRefs[i];
                    var tmp = Path.Combine(bundle.WorkDir, $"verify-{i}.bin");
                    await using (var dst = File.Create(tmp))
                        await _transport.DownloadAsync(blob, dst, null, ct);
                    var rsha = await ArchiveBundleBuilder.ComputeSha256Async(tmp, ct);
                    File.Delete(tmp);
                    if (!string.Equals(rsha, part.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Round-trip sha256 mismatch on part {i + 1}: " +
                                              $"sent={part.Sha256Hex}, downloaded={rsha}.");
                }
                verified = true;
            }

            // Record manifest BEFORE deleting anything local.
            var totalSha = ComposeTotalSha(bundle.Parts);
            var totalBytes = bundle.Parts.Sum(p => p.SizeBytes);
            var entry = new ArchiveManifestEntry(
                Id: 0,
                PeriodLabel: periodLabel,
                FromUtc: fromUtc, ToUtc: toUtc,
                Transport: _transport.Name,
                Target: target,
                Parts: uploadedRefs,
                TotalSha256Hex: totalSha,
                RowsQuotes: bundle.RowsQuotes,
                RowsBars: bundle.RowsBars,
                RowsTrades: bundle.RowsTrades,
                TotalBytes: totalBytes,
                UploadedUtc: DateTime.UtcNow,
                DeletedLocal: false,
                RowsDepth: bundle.RowsDepth);
            var id = _manifest.Insert(entry);
            entry = entry with { Id = id };

            var deleted = false;
            if (opts.DeleteLocalAfterArchive && verified)
            {
                progress?.Report("Pruning local rows…");
                var q = opts.Tables.HasFlag(ArchiveTables.Quotes)
                    ? await _store.DeleteQuotesInRangeAsync(fromUtc, toUtc, ct) : 0;
                var b = opts.Tables.HasFlag(ArchiveTables.Bars)
                    ? await _store.DeleteBarsInRangeAsync(fromUtc, toUtc, ct) : 0;
                var t = opts.Tables.HasFlag(ArchiveTables.Trades)
                    ? await _store.DeleteTradesInRangeAsync(fromUtc, toUtc, ct) : 0;
                var d = opts.Tables.HasFlag(ArchiveTables.Depth)
                    ? await _store.DeleteDepthInRangeAsync(fromUtc, toUtc, ct) : 0;
                _manifest.MarkLocalDeleted(id);
                entry = entry with { DeletedLocal = true };
                progress?.Report($"Pruned: {Pruned(q)} quotes, {b:n0} bars, {Pruned(t)} trades, {Pruned(d)} depth.");
                deleted = true;
            }
            else if (opts.DeleteLocalAfterArchive && !verified)
            {
                progress?.Report("Skipping prune — verification disabled, refusing to delete unverified.");
            }

            progress?.Report($"Archive #{id} complete.");
            return new ArchiveResult(entry, verified, deleted);
        }
        finally
        {
            // Always clean up the staging working directory.
            try { if (Directory.Exists(bundle.WorkDir)) Directory.Delete(bundle.WorkDir, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean staging dir {Dir}", bundle.WorkDir); }
        }
    }

    public Task<IReadOnlyList<ArchiveManifestEntry>> ListArchivesAsync(
        string? transport = null, int maxRows = 200, CancellationToken ct = default) =>
        Task.FromResult(_manifest.List(transport, maxRows));

    public async Task RestoreAsync(
        ArchiveManifestEntry entry,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (entry.Transport != _transport.Name)
            throw new InvalidOperationException(
                $"Archive #{entry.Id} was uploaded via '{entry.Transport}', but the active transport is '{_transport.Name}'.");

        var opts = _options.CurrentValue;
        var stagingRoot = opts.StagingDirectory ?? DefaultStaging();
        var workDir = Path.Combine(stagingRoot, $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            // Download every part.
            var partPaths = new List<string>();
            for (var i = 0; i < entry.Parts.Count; i++)
            {
                var blob = entry.Parts[i];
                var dst = Path.Combine(workDir, blob.PartName);
                progress?.Report($"Downloading part {i + 1}/{entry.Parts.Count} ({Fmt(blob.SizeBytes)})…");
                await using (var fs = File.Create(dst))
                    await _transport.DownloadAsync(blob, fs, null, ct);
                partPaths.Add(dst);
            }

            // Reassemble (cat) into one zip.
            var zipPath = Path.Combine(workDir, "bundle.zip");
            await using (var dst = File.Create(zipPath))
            {
                foreach (var p in partPaths)
                {
                    await using var src = File.OpenRead(p);
                    await src.CopyToAsync(dst, ct);
                }
            }

            // Unzip and re-import every parquet.
            progress?.Report("Unpacking and re-importing parquet files…");
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("Archive bundle is missing manifest.json.");
            BundleManifest bundleManifest;
            using (var ms = manifestEntry.Open())
            using (var sr = new StreamReader(ms))
                bundleManifest = JsonSerializer.Deserialize<BundleManifest>(await sr.ReadToEndAsync(ct))
                    ?? throw new InvalidDataException("manifest.json failed to deserialize.");

            foreach (var file in bundleManifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var entryInZip = zip.GetEntry(file.Path)
                    ?? throw new InvalidDataException($"Bundle missing parquet entry '{file.Path}'.");
                var tmp = Path.Combine(workDir, Guid.NewGuid().ToString("N") + ".parquet");
                using (var src = entryInZip.Open())
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);

                switch (file.Kind)
                {
                    case "quotes": await ImportQuotesAsync(tmp, ct); break;
                    case "bars": await ImportBarsAsync(tmp, ct); break;
                    case "trades": await ImportTradesAsync(tmp, ct); break;
                    case "depth": await ImportDepthAsync(tmp, ct); break;
                }
                File.Delete(tmp);
                progress?.Report($"Restored {file.Kind}: {file.Rows:n0} rows from {file.Path}.");
            }
            await _store.FlushAsync(ct);
            progress?.Report("Restore complete.");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to clean restore dir {Dir}", workDir); }
        }
    }

    private async Task ImportQuotesAsync(string parquetPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(parquetPath);
        var rows = await ParquetSerializer.DeserializeAsync<QuoteParquetRow>(fs, null, ct);
        foreach (var r in rows)
        {
            _store.EnqueueQuote(new Quote(
                new InstrumentId((int)r.InstrumentId),
                FromMicros(r.EventTimeMicros), FromMicros(r.IngestTimeMicros),
                r.Bid, r.Ask, r.BidSize, r.AskSize,
                (BrokerKind)r.Source, r.Sequence, r.EventTimeApproximate));
        }
    }

    private async Task ImportBarsAsync(string parquetPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(parquetPath);
        var rows = await ParquetSerializer.DeserializeAsync<BarParquetRow>(fs, null, ct);
        foreach (var r in rows)
        {
            _store.EnqueueBar(new OhlcvBar(
                new InstrumentId((int)r.InstrumentId), (BarSize)r.BarSize,
                FromMicros(r.OpenTimeMicros), r.Open, r.High, r.Low, r.Close, r.Volume,
                (BrokerKind)r.Source, r.IsFinal));
        }
    }

    private async Task ImportTradesAsync(string parquetPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(parquetPath);
        var rows = await ParquetSerializer.DeserializeAsync<TradeParquetRow>(fs, null, ct);
        foreach (var r in rows)
        {
            _store.EnqueueTrade(new TradePrint(
                new InstrumentId((int)r.InstrumentId),
                FromMicros(r.EventTimeMicros), FromMicros(r.IngestTimeMicros),
                r.Price, r.Size, (AggressorSide)r.Aggressor,
                (BrokerKind)r.Source, r.Sequence, r.EventTimeApproximate));
        }
    }

    private async Task ImportDepthAsync(string parquetPath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(parquetPath);
        var rows = await ParquetSerializer.DeserializeAsync<DepthParquetRow>(fs, null, ct);

        // Rows arrive grouped per snapshot (bids then asks, ascending level) in event-time order —
        // the same shape ExportDepthAsync wrote. Regroup consecutive rows sharing (instrument,
        // event time) back into one snapshot and re-enqueue.
        long curId = long.MinValue, curEvent = long.MinValue;
        int curSource = 0;
        var bids = new List<DepthLevel>();
        var asks = new List<DepthLevel>();

        void Flush()
        {
            if (curId == long.MinValue) return;
            _store.EnqueueDepth(
                new InstrumentId((int)curId),
                new DepthSnapshot(FromMicros(curEvent), bids.ToArray(), asks.ToArray()),
                (BrokerKind)curSource);
            bids = new List<DepthLevel>();
            asks = new List<DepthLevel>();
        }

        foreach (var r in rows)
        {
            if (r.InstrumentId != curId || r.EventTimeMicros != curEvent)
            {
                Flush();
                curId = r.InstrumentId;
                curEvent = r.EventTimeMicros;
                curSource = r.Source;
            }
            var level = new DepthLevel(r.Price, r.Size);
            if (r.Side == "B") bids.Add(level); else asks.Add(level);
        }
        Flush();
    }

    /// <summary>Format a prune row count: QuestDB's partition drop reports an unknown count (-1).</summary>
    private static string Pruned(long n) => n < 0 ? "partition(s) of" : n.ToString("n0");

    private static string ComposeTotalSha(IReadOnlyList<BundlePart> parts)
    {
        if (parts.Count == 1) return parts[0].Sha256Hex;
        using var sha = SHA256.Create();
        foreach (var p in parts)
        {
            var bytes = Convert.FromHexString(p.Sha256Hex);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static string BuildPeriodLabel(DateTime fromUtc, DateTime toUtc, ArchivePeriod period) =>
        period switch
        {
            ArchivePeriod.Weekly => BuildWeekLabel(fromUtc),
            ArchivePeriod.Monthly => $"{fromUtc:yyyy}-{fromUtc:MM}",
            _ => $"{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}",
        };

    private static string BuildWeekLabel(DateTime fromUtc)
    {
        var cal = CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(fromUtc, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        return $"{fromUtc:yyyy}-W{week:D2}";
    }

    private static string DefaultStaging() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DaxAlgoTerminal", "archive-staging");

    private static DateTime FromMicros(long micros) => DateTime.UnixEpoch.AddTicks(micros * 10L);

    private static string Fmt(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
}
