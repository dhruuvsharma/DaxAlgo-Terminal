namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Which stream a <see cref="SqliteMarketDataStore"/> instance owns. The single-file backend uses
/// <see cref="All"/> (identity registry + quotes/trades/bars in one file). The per-broker backend
/// gives each broker one file per stream — <see cref="Quotes"/> (<c>…-l1.db</c>),
/// <see cref="Trades"/> (<c>…-trades.db</c>), <see cref="Bars"/> (<c>…-bars.db</c>), and
/// <see cref="Depth"/> (<c>…-l2.db</c>) — so each gets its own writer and can be pruned or wiped
/// independently. Depth is persisted <em>only</em> by a <see cref="Depth"/>-scoped store.
/// </summary>
internal enum SqliteStoreStream
{
    All,
    Quotes,
    Trades,
    Bars,
    Depth,
}
