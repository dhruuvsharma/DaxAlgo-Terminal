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

        var stagingRoot = string.IsNullOrWhiteSpace(opts.StagingDirectory) ? DefaultStaging() : opts.StagingDirectory;
        var builder = new ArchiveBundleBuilder(_store, _registry, _logger);

        progress?.Report($"Building documents for [{fromUtc:s} → {toUtc:s})…");
        var bundle = await builder.BuildAsync(fromUtc, toUtc, opts.Tables, stagingRoot, progress, ct);

        try
        {
            // Upload each labelled document as its own Telegram file, splitting only the ones that
            // alone exceed the cap. Each uploaded blob is stamped with its document identity so the
            // restorer can regroup parts and re-import without an inner manifest.
            var uploadedRefs = new List<ArchiveBlobRef>();
            var periodLabel = BuildPeriodLabel(fromUtc, toUtc, opts.Period);
            var docNum = 0;
            foreach (var doc in bundle.Documents)
            {
                docNum++;
                if (doc.SizeBytes <= opts.MaxPartBytes)
                {
                    progress?.Report($"[{docNum}/{bundle.Documents.Count}] Uploading {doc.DisplayName} " +
                                     $"({doc.Rows:n0} rows, {Fmt(doc.SizeBytes)})…");
                    await using var fs = File.OpenRead(doc.LocalPath);
                    var blob = await _transport.UploadAsync(fs, doc.DisplayName, doc.SizeBytes, target, null, ct);
                    uploadedRefs.Add(StampDocMeta(blob, doc, partIndex: 1, partCount: 1, sha: doc.Sha256Hex));
                }
                else
                {
                    var slices = await ArchiveBundleBuilder.SplitFileAsync(doc.LocalPath, bundle.WorkDir, opts.MaxPartBytes, ct);
                    progress?.Report($"[{docNum}/{bundle.Documents.Count}] {doc.DisplayName} is {Fmt(doc.SizeBytes)} " +
                                     $"— splitting into {slices.Count} parts (cap = {Fmt(opts.MaxPartBytes)})…");
                    for (var i = 0; i < slices.Count; i++)
                    {
                        var slice = slices[i];
                        progress?.Report($"    Uploading {slice.Name} ({Fmt(slice.SizeBytes)})…");
                        await using var fs = File.OpenRead(slice.LocalPath);
                        var blob = await _transport.UploadAsync(fs, slice.Name, slice.SizeBytes, target, null, ct);
                        uploadedRefs.Add(StampDocMeta(blob, doc, partIndex: i + 1, partCount: slices.Count, sha: slice.Sha256Hex));
                    }
                }
            }

            // Verify round-trip (optional but on by default): re-download each uploaded blob and
            // confirm its sha256 matches what we sent.
            var verified = false;
            if (opts.VerifyAfterUpload)
            {
                progress?.Report("Verifying upload checksums…");
                for (var i = 0; i < uploadedRefs.Count; i++)
                {
                    var blob = uploadedRefs[i];
                    var tmp = Path.Combine(bundle.WorkDir, $"verify-{i}.bin");
                    await using (var dst = File.Create(tmp))
                        await _transport.DownloadAsync(blob, dst, null, ct);
                    var rsha = await ArchiveBundleBuilder.ComputeSha256Async(tmp, ct);
                    File.Delete(tmp);
                    if (!string.Equals(rsha, blob.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Round-trip sha256 mismatch on '{blob.PartName}': " +
                                              $"sent={blob.Sha256Hex}, downloaded={rsha}.");
                }
                verified = true;
            }

            // Record manifest BEFORE deleting anything local.
            var totalSha = ComposeTotalSha(uploadedRefs.Select(r => r.Sha256Hex));
            var totalBytes = uploadedRefs.Sum(p => p.SizeBytes);
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

    public async Task<IReadOnlyList<ArchiveCoverageWindow>> GetCoverageAsync(CancellationToken ct = default)
    {
        var windows = await ComputeWindowsAsync(ct).ConfigureAwait(false);
        windows.Reverse(); // newest window first for display
        return windows;
    }

    public async Task<InstantOffloadResult> OffloadPendingAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!_transport.IsReady)
            throw new InvalidOperationException(
                $"Transport '{_transport.Name}' is not ready — log in to Telegram first (Data → Market data archive).");

        var pending = (await ComputeWindowsAsync(ct).ConfigureAwait(false))
            .Where(w => !w.Offloaded)
            .OrderBy(w => w.FromUtc)
            .ToList();

        if (pending.Count == 0)
        {
            progress?.Report("Nothing to offload — all local data is already on Telegram.");
            return new InstantOffloadResult(0, 0, 0, 0);
        }

        var target = BuildDefaultTarget(_options.CurrentValue);
        progress?.Report($"{pending.Count} pending window(s) to offload…");

        int archived = 0, failed = 0;
        long bytes = 0;
        foreach (var w in pending)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(
                $"[{archived + failed + 1}/{pending.Count}] {w.PeriodLabel} " +
                $"[{w.FromUtc:yyyy-MM-dd} → {w.ToUtc:yyyy-MM-dd})…");
            try
            {
                var r = await ArchiveRangeAsync(w.FromUtc, w.ToUtc, target, progress, ct).ConfigureAwait(false);
                archived++;
                bytes += r.Entry.TotalBytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Instant offload failed for window {Label} [{From:o} → {To:o})",
                    w.PeriodLabel, w.FromUtc, w.ToUtc);
                progress?.Report($"  ↳ failed: {ex.Message}");
            }
        }

        progress?.Report($"Instant offload complete — {archived} offloaded, {failed} failed, {Fmt(bytes)} shipped.");
        return new InstantOffloadResult(archived, pending.Count, failed, bytes);
    }

    /// <summary>Period-aligned windows spanning the local data extent (oldest first), each labelled
    /// against the manifest. The current in-progress period is excluded (data still arriving).</summary>
    private async Task<List<ArchiveCoverageWindow>> ComputeWindowsAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var extent = await _store.GetDataExtentAsync(ct).ConfigureAwait(false);
        var windows = new List<ArchiveCoverageWindow>();
        if (!extent.HasData) return windows;

        foreach (var (from, to) in ArchivePeriodMath.ClosedWindows(extent.EarliestUtc!.Value, DateTime.UtcNow, opts.Period))
        {
            ct.ThrowIfCancellationRequested();
            var coverId = _manifest.FindCovering(from, to, _transport.Name);
            windows.Add(new ArchiveCoverageWindow(
                BuildPeriodLabel(from, to, opts.Period), from, to, coverId is not null, coverId));
        }
        return windows;
    }

    private static ArchiveTarget BuildDefaultTarget(ArchiveOptions opts) =>
        string.Equals(opts.DefaultTargetKind, "chat", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(opts.DefaultTargetChatRef)
            ? ArchiveTarget.Chat(opts.DefaultTargetChatRef!.Trim())
            : ArchiveTarget.SavedMessages;

    public async Task RestoreAsync(
        ArchiveManifestEntry entry,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (entry.Transport != _transport.Name)
            throw new InvalidOperationException(
                $"Archive #{entry.Id} was uploaded via '{entry.Transport}', but the active transport is '{_transport.Name}'.");

        // Per-document archives stamp ArchiveDocMeta.Format into every blob; older single-zip bundles
        // don't — fall back to the legacy concat-unzip path for those so old archives still restore.
        var isPerDocument = entry.Parts.Any(p =>
            p.Metadata.TryGetValue(ArchiveDocMeta.Format, out var f) && f == ArchiveDocMeta.PerDocument);
        if (isPerDocument)
            await RestorePerDocumentAsync(entry, progress, ct);
        else
            await RestoreLegacyZipAsync(entry, progress, ct);
    }

    /// <summary>Restore a per-document archive: regroup each document's parts by <see cref="ArchiveDocMeta.DocKey"/>,
    /// download + concatenate them back into the original parquet, then re-import by declared kind.</summary>
    private async Task RestorePerDocumentAsync(
        ArchiveManifestEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var stagingRoot = string.IsNullOrWhiteSpace(opts.StagingDirectory) ? DefaultStaging() : opts.StagingDirectory;
        var workDir = Path.Combine(stagingRoot, $"restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var documents = entry.Parts
                .GroupBy(p => p.Metadata.TryGetValue(ArchiveDocMeta.DocKey, out var k) ? k : p.PartName)
                .ToList();
            var docNum = 0;
            foreach (var docParts in documents)
            {
                ct.ThrowIfCancellationRequested();
                docNum++;
                var ordered = docParts
                    .OrderBy(p => int.TryParse(p.Metadata.GetValueOrDefault(ArchiveDocMeta.PartIndex), out var ix) ? ix : 0)
                    .ToList();
                var kind = ordered[0].Metadata.GetValueOrDefault(ArchiveDocMeta.Kind, "");

                progress?.Report($"[{docNum}/{documents.Count}] Downloading {docParts.Key}" +
                                 (ordered.Count > 1 ? $" ({ordered.Count} parts)…" : "…"));
                var parquetPath = Path.Combine(workDir, $"{Guid.NewGuid():N}.parquet");
                await using (var dst = File.Create(parquetPath))
                    foreach (var part in ordered)
                        await _transport.DownloadAsync(part, dst, null, ct);

                long rows = kind switch
                {
                    "quotes" => await ImportQuotesAsync(parquetPath, ct),
                    "bars" => await ImportBarsAsync(parquetPath, ct),
                    "trades" => await ImportTradesAsync(parquetPath, ct),
                    "depth" => await ImportDepthAsync(parquetPath, ct),
                    _ => 0,
                };
                File.Delete(parquetPath);
                progress?.Report($"Restored {kind}: {rows:n0} rows from {docParts.Key}.");
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

    /// <summary>Restore a legacy single-zip bundle: download all parts, concatenate into one zip,
    /// unzip, read the inner manifest.json, and re-import each parquet.</summary>
    private async Task RestoreLegacyZipAsync(
        ArchiveManifestEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var stagingRoot = string.IsNullOrWhiteSpace(opts.StagingDirectory) ? DefaultStaging() : opts.StagingDirectory;
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

    private async Task<long> ImportQuotesAsync(string parquetPath, CancellationToken ct)
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
        return rows.Count;
    }

    private async Task<long> ImportBarsAsync(string parquetPath, CancellationToken ct)
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
        return rows.Count;
    }

    private async Task<long> ImportTradesAsync(string parquetPath, CancellationToken ct)
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
        return rows.Count;
    }

    private async Task<long> ImportDepthAsync(string parquetPath, CancellationToken ct)
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
        return rows.Count;
    }

    /// <summary>Format a prune row count: QuestDB's partition drop reports an unknown count (-1).</summary>
    private static string Pruned(long n) => n < 0 ? "partition(s) of" : n.ToString("n0");

    /// <summary>One sha256 over the concatenation of every part's sha256 — a stable digest of the
    /// whole archive regardless of how many documents/parts it spans.</summary>
    private static string ComposeTotalSha(IEnumerable<string> partHexes)
    {
        var hexes = partHexes.ToList();
        if (hexes.Count == 1) return hexes[0];
        using var sha = SHA256.Create();
        foreach (var hex in hexes)
        {
            var bytes = Convert.FromHexString(hex);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Copy the transport's blob ref and stamp the document identity + part position into its
    /// metadata (preserving the transport's own keys), plus the known sha256 used for verification.</summary>
    private static ArchiveBlobRef StampDocMeta(
        ArchiveBlobRef blob, BuiltDocument doc, int partIndex, int partCount, string sha)
    {
        var meta = new Dictionary<string, string>(blob.Metadata, StringComparer.Ordinal)
        {
            [ArchiveDocMeta.Format] = ArchiveDocMeta.PerDocument,
            [ArchiveDocMeta.Kind] = doc.Kind,
            [ArchiveDocMeta.DocKey] = doc.DisplayName,
            [ArchiveDocMeta.PartIndex] = partIndex.ToString(),
            [ArchiveDocMeta.PartCount] = partCount.ToString(),
            [ArchiveDocMeta.InstrumentId] = doc.InstrumentId.ToString(),
            [ArchiveDocMeta.Symbol] = doc.Symbol,
            [ArchiveDocMeta.Exchange] = doc.Exchange,
            [ArchiveDocMeta.Broker] = doc.Broker,
            [ArchiveDocMeta.BarSize] = doc.BarSizeLabel,
            [ArchiveDocMeta.Rows] = doc.Rows.ToString(),
        };
        return blob with { Sha256Hex = sha, Metadata = meta };
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
