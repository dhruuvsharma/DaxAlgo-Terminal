namespace TradingTerminal.Core.MarketData.Archive;

/// <summary>
/// High-level archive orchestrator. Subscribers see "archive this range", "list past archives",
/// and "restore an archive" — the heavy lifting (exporting store rows, packing parquet, splitting
/// the zip, verifying round-trip, pruning local data) lives behind this seam.
/// </summary>
public interface IMarketDataArchiver
{
    /// <summary>Export, upload, verify, and (per options) prune local rows for the range.
    /// Idempotent against the manifest — if an entry already exists for the same exact range,
    /// returns the existing one without re-uploading.</summary>
    Task<ArchiveResult> ArchiveRangeAsync(
        DateTime fromUtc, DateTime toUtc,
        ArchiveTarget target,
        IProgress<string>? progress,
        CancellationToken ct);

    /// <summary>Past archive entries (newest first), optionally filtered by transport.</summary>
    Task<IReadOnlyList<ArchiveManifestEntry>> ListArchivesAsync(
        string? transport = null, int maxRows = 200, CancellationToken ct = default);

    /// <summary>Every closed period-aligned window of the local data span, each labelled offloaded or
    /// pending against the manifest — the "which data is on Telegram and which isn't" view. Newest
    /// window first.</summary>
    Task<IReadOnlyList<ArchiveCoverageWindow>> GetCoverageAsync(CancellationToken ct = default);

    /// <summary>Instantly offload everything not yet on Telegram: archive every pending window (each
    /// export is split into ≤2 GB parts, verified, and pruned per the usual flow) to the configured
    /// default target. Idempotent — already-offloaded windows are skipped.</summary>
    Task<InstantOffloadResult> OffloadPendingAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Download all parts for an archive, reassemble, unzip, and bulk-load every parquet
    /// back into the store. Skips rows already present (the store's upserts handle bars; quotes
    /// rely on the primary-key constraint).</summary>
    Task RestoreAsync(
        ArchiveManifestEntry entry,
        IProgress<string>? progress,
        CancellationToken ct);
}
