using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Knobs for the market-data archive subsystem. Bound from the <c>MarketDataArchive</c> section
/// of appsettings. Mutable on the user's end via the Archive Settings dialog.
/// </summary>
public sealed class ArchiveOptions
{
    public const string SectionName = "MarketDataArchive";

    /// <summary>Master switch — when false the schedule service idles and the offload commands no-op.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often the schedule rolls over.</summary>
    public ArchivePeriod Period { get; set; } = ArchivePeriod.Weekly;

    /// <summary>Tables to include in each bundle (bit flag). Default is Quotes + Bars.</summary>
    public ArchiveTables Tables { get; set; } = ArchiveTables.Quotes | ArchiveTables.Bars;

    /// <summary>UTC hour of day at which the schedule service checks for a rollover (0–23).</summary>
    public int DailyCheckHourUtc { get; set; } = 3;

    /// <summary>Soft cap on one uploaded part. Telegram MTProto allows 2 GB; we leave headroom
    /// because zip framing and base hashes can push a near-cap part over the limit.</summary>
    public long MaxPartBytes { get; set; } = 1_900_000_000L;

    /// <summary>Re-download every uploaded part and verify its sha256 before pruning local rows.
    /// Costs 2x bandwidth on upload day but is the only way to know the bytes survived the wire.</summary>
    public bool VerifyAfterUpload { get; set; } = true;

    /// <summary>When true, rows in the archived range are deleted from the local store after
    /// verification succeeds.</summary>
    public bool DeleteLocalAfterArchive { get; set; } = true;

    /// <summary>Default destination when neither the schedule nor a manual call specifies one.</summary>
    public string DefaultTargetKind { get; set; } = "saved";

    /// <summary>Used when <see cref="DefaultTargetKind"/> = "chat" — Telegram username (with or
    /// without @), an invite link, or a numeric chat id.</summary>
    public string? DefaultTargetChatRef { get; set; }

    /// <summary>Temp directory for staging parquet bundles before upload. Falls back to
    /// %LocalAppData%/DaxAlgoTerminal/archive-staging when null.</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>Path to the archive manifest SQLite file. Falls back to
    /// %LocalAppData%/DaxAlgoTerminal/archive-manifest.db when null.</summary>
    public string? ManifestDatabasePath { get; set; }
}
