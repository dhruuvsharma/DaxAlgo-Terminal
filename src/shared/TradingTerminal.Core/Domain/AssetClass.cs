namespace TradingTerminal.Core.Domain;

/// <summary>
/// Broker-neutral asset classification for a canonical instrument. Derived from a broker's
/// native security type during ingest (IB <c>SecType</c>, Alpaca asset class, cTrader symbol
/// category) so strategies can reason about an instrument without knowing which broker sourced it.
/// </summary>
public enum AssetClass
{
    Unknown = 0,
    Equity,
    Future,
    Forex,
    Crypto,
    Option,
    Index,
}
