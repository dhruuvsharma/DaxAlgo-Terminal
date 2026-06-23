namespace TradingTerminal.Infrastructure.Backtest.Persistence;

/// <summary>
/// Wire format for trade prints written to parquet — the optional <b>trade tape</b> for a
/// parquet-source backtest, replayed alongside the quote parquet so trade-tape-primary strategies
/// (e.g. SigmaIcFlow) see genuine aggressor flow instead of synthetic L1. Timestamps are epoch
/// microseconds UTC (same convention as <see cref="TickRecord"/>). <see cref="Aggressor"/> encodes
/// the taker side: 1 = Buy (lifted the offer), 2 = Sell (hit the bid), 0 = Unknown.
/// </summary>
internal sealed class TradeRecord
{
    public long TimestampMicros { get; set; }
    public double Price { get; set; }
    public long Size { get; set; }
    public int Aggressor { get; set; }
}
