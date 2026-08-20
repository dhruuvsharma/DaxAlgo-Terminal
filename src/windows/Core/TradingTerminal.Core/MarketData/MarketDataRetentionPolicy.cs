using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Core.MarketData;

/// <summary>Which stored stream a retention cut applies to.</summary>
public enum MarketDataStream
{
    Quotes,
    Trades,
    Bars,
    Depth,
}

/// <summary>
/// One deletion the retention sweep should perform: everything in <c>[FromUtc, ToUtc)</c> of one
/// stream.
/// </summary>
/// <param name="Stream">The stream to delete from.</param>
/// <param name="FromUtc">Lower bound. The Unix epoch in practice — there is no older market data.</param>
/// <param name="ToUtc">Exclusive cutoff. Everything at or after this is kept.</param>
/// <param name="ClampedByPendingArchive">
/// True when <paramref name="ToUtc"/> was pulled back to protect data the archive has not shipped.
/// Worth logging: it explains why the store is larger than the configured window.
/// </param>
public readonly record struct MarketDataRetentionCut(
    MarketDataStream Stream,
    DateTime FromUtc,
    DateTime ToUtc,
    bool ClampedByPendingArchive);

/// <summary>
/// Works out what retention should delete. Pure — no store, no clock, no I/O — because every
/// interesting decision here is a boundary condition, and boundary conditions are worth testing
/// without a database.
/// </summary>
public static class MarketDataRetentionPolicy
{
    /// <summary>The lower bound of every cut. Market data predating this does not exist.</summary>
    public static readonly DateTime Floor = DateTime.UnixEpoch;

    /// <summary>
    /// Builds the deletions for one sweep.
    /// </summary>
    /// <param name="options">Per-stream windows and the sweep's own switches.</param>
    /// <param name="nowUtc">Current time; cutoffs are measured back from here.</param>
    /// <param name="earliestStoredUtc">
    /// Oldest row in the store, or null when it is empty. Used only to skip work — there is nothing to
    /// delete when the oldest data is already newer than every cutoff.
    /// </param>
    /// <param name="oldestPendingArchiveUtc">
    /// Start of the oldest window the archive still owes, or null when nothing is pending, archiving is
    /// off, or no archive has ever succeeded. Cutoffs never pass this.
    /// </param>
    /// <returns>The cuts to perform, in stream order. Empty when there is nothing to do.</returns>
    public static IReadOnlyList<MarketDataRetentionCut> Plan(
        MarketDataStoreOptions options,
        DateTime nowUtc,
        DateTime? earliestStoredUtc,
        DateTime? oldestPendingArchiveUtc)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.RetentionSweepEnabled)
            return [];

        var cuts = new List<MarketDataRetentionCut>(4);
        Add(cuts, MarketDataStream.Quotes, options.QuoteRetentionDays);
        Add(cuts, MarketDataStream.Trades, options.TradeRetentionDays);
        Add(cuts, MarketDataStream.Bars, options.BarRetentionDays);
        Add(cuts, MarketDataStream.Depth, options.DepthRetentionDays);
        return cuts;

        void Add(List<MarketDataRetentionCut> into, MarketDataStream stream, int days)
        {
            // Zero or negative is "keep forever", not "delete everything". Getting this backwards
            // would silently wipe the store the first time someone typed 0 into a settings box.
            if (days <= 0)
                return;

            var cutoff = nowUtc.AddDays(-days);
            var clamped = false;

            if (options.RespectPendingArchives &&
                oldestPendingArchiveUtc is { } pending &&
                pending < cutoff)
            {
                cutoff = pending;
                clamped = true;
            }

            if (cutoff <= Floor)
                return;

            // Nothing older than the cutoff, so the delete would be a no-op scan. Skipping is what
            // keeps a steady-state sweep free once retention has caught up.
            //
            // A NULL extent skips too. It means the store reported nothing — an empty database, an
            // unreachable QuestDB, or the per-broker store with no files opened yet — and in every
            // one of those cases the delete would reach the same nothing. The next sweep picks it up
            // once the store is actually live.
            if (earliestStoredUtc is not { } earliest || earliest >= cutoff)
                return;

            into.Add(new MarketDataRetentionCut(stream, Floor, cutoff, clamped));
        }
    }

    /// <summary>
    /// The archive floor: the start of the oldest window still owed, or null when retention should not
    /// be held back at all.
    /// </summary>
    /// <param name="archivingEnabled">Whether the scheduled archive is switched on.</param>
    /// <param name="hasSucceededBefore">
    /// Whether any archive has ever completed. When nothing ever has, archiving is not really running —
    /// respecting its backlog would stop retention forever, which is the failure this guards against.
    /// </param>
    /// <param name="pendingWindowStarts">Start times of windows not yet shipped.</param>
    public static DateTime? ArchiveFloor(
        bool archivingEnabled,
        bool hasSucceededBefore,
        IEnumerable<DateTime> pendingWindowStarts)
    {
        ArgumentNullException.ThrowIfNull(pendingWindowStarts);
        if (!archivingEnabled || !hasSucceededBefore)
            return null;

        DateTime? oldest = null;
        foreach (var start in pendingWindowStarts)
        {
            if (oldest is null || start < oldest)
                oldest = start;
        }

        return oldest;
    }
}
