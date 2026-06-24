namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Converts between <see cref="DateTime"/> (UTC) and epoch microseconds — the on-disk timestamp
/// format for the canonical store. Microsecond resolution matches the existing parquet tick
/// format and keeps timestamps integer-comparable and timezone-agnostic.
/// </summary>
internal static class EpochTime
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    public static long ToMicros(DateTime utc)
    {
        var ticks = (utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()).Ticks;
        return (ticks - UnixEpochTicks) / 10; // 10 ticks per microsecond
    }

    public static DateTime FromMicros(long micros) =>
        new(UnixEpochTicks + micros * 10, DateTimeKind.Utc);
}
