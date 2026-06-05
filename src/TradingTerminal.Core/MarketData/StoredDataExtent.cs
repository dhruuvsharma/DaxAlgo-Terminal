namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The earliest and latest event time present in the local store across the persisted tables.
/// Used by the archive coverage / instant-offload feature to decide which periods still hold
/// data that hasn't been shipped to Telegram. Both bounds are null when the store is empty.
/// </summary>
public sealed record StoredDataExtent(DateTime? EarliestUtc, DateTime? LatestUtc)
{
    public static StoredDataExtent Empty { get; } = new(null, null);

    /// <summary>True when there's at least one row, i.e. both bounds are set.</summary>
    public bool HasData => EarliestUtc is not null && LatestUtc is not null;

    /// <summary>Union of two extents — the min of the earliests and max of the latests, ignoring nulls.</summary>
    public static StoredDataExtent Combine(StoredDataExtent a, StoredDataExtent b) =>
        new(MinNullable(a.EarliestUtc, b.EarliestUtc), MaxNullable(a.LatestUtc, b.LatestUtc));

    private static DateTime? MinNullable(DateTime? x, DateTime? y) =>
        x is null ? y : y is null ? x : (x < y ? x : y);

    private static DateTime? MaxNullable(DateTime? x, DateTime? y) =>
        x is null ? y : y is null ? x : (x > y ? x : y);
}
