namespace TradingTerminal.Core.MarketData.Archive;

/// <summary>How often the schedule rolls over and produces a new archive.</summary>
public enum ArchivePeriod
{
    Weekly = 0,
    Monthly = 1,
}

/// <summary>Which logical table to include in an archive bundle.</summary>
[Flags]
public enum ArchiveTables
{
    None = 0,
    Quotes = 1 << 0,
    Bars = 1 << 1,
    Trades = 1 << 2,

    /// <summary>L2 order-book snapshots. Only present when the store backend persists depth
    /// (QuestDB); on SQLite/Postgres there's nothing to export so this flag is a no-op there.</summary>
    Depth = 1 << 3,
}

/// <summary>Where in Telegram an archive goes — Saved Messages by default, or a named chat.</summary>
public sealed record ArchiveTarget(string Kind, string? ChatRef)
{
    public static ArchiveTarget SavedMessages { get; } = new("saved", null);
    public static ArchiveTarget Chat(string chatRef) => new("chat", chatRef);
    public bool IsSavedMessages => Kind == "saved";
}

/// <summary>An opaque reference to one uploaded part. Transports stamp their own identifiers
/// (Telegram message id + access hash) into <see cref="Metadata"/> so the manifest can round-trip
/// the download later.</summary>
public sealed record ArchiveBlobRef(
    string TransportName,
    string PartName,
    long SizeBytes,
    string Sha256Hex,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>One row in the archive history. Spans 1+ uploaded parts that together reconstruct
/// the bundle for <see cref="FromUtc"/>..<see cref="ToUtc"/>.</summary>
public sealed record ArchiveManifestEntry(
    long Id,
    string PeriodLabel,
    DateTime FromUtc,
    DateTime ToUtc,
    string Transport,
    ArchiveTarget Target,
    IReadOnlyList<ArchiveBlobRef> Parts,
    string TotalSha256Hex,
    long RowsQuotes,
    long RowsBars,
    long RowsTrades,
    long TotalBytes,
    DateTime UploadedUtc,
    bool DeletedLocal,
    long RowsDepth = 0);

/// <summary>Outcome of a single ArchiveRange call.</summary>
public sealed record ArchiveResult(
    ArchiveManifestEntry Entry,
    bool VerifiedRoundTrip,
    bool LocalDataDeleted);

/// <summary>UTC boundary math for archive periods. Both the scheduler and the UI use this so
/// "last week" / "last month" mean the same thing in every code path.</summary>
public static class ArchivePeriodMath
{
    public static (DateTime FromUtc, DateTime ToUtc) ClosedPeriod(DateTime nowUtc, ArchivePeriod period) =>
        period switch
        {
            ArchivePeriod.Weekly => ClosedWeek(nowUtc),
            ArchivePeriod.Monthly => ClosedMonth(nowUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };

    private static (DateTime, DateTime) ClosedWeek(DateTime nowUtc)
    {
        var today = nowUtc.Date;
        int daysFromMonday = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var thisMondayUtc = DateTime.SpecifyKind(today.AddDays(-daysFromMonday), DateTimeKind.Utc);
        return (thisMondayUtc.AddDays(-7), thisMondayUtc);
    }

    private static (DateTime, DateTime) ClosedMonth(DateTime nowUtc)
    {
        var firstOfThisMonth = DateTime.SpecifyKind(new DateTime(nowUtc.Year, nowUtc.Month, 1), DateTimeKind.Utc);
        return (firstOfThisMonth.AddMonths(-1), firstOfThisMonth);
    }
}
