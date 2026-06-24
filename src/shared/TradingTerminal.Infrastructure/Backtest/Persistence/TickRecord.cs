namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Wire format for ticks written to parquet. Timestamps are epoch microseconds UTC so
/// the file format is timezone-agnostic and survives DateTimeKind round-trips. Sizes are
/// <c>long</c> to match <see cref="Core.Domain.Tick"/>; for instruments that don't publish
/// L1 sizes, write 0.
/// </summary>
internal sealed class TickRecord
{
    public long TimestampMicros { get; set; }
    public double Bid { get; set; }
    public double Ask { get; set; }
    public long BidSize { get; set; }
    public long AskSize { get; set; }
}
