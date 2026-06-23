namespace TradingTerminal.Core.Domain;

/// <summary>An IB-style instrument descriptor (intentionally aligned with TWS API fields).</summary>
public sealed record Contract(
    string Symbol,
    string SecType,
    string Exchange,
    string Currency,
    string PrimaryExchange)
{
    public static Contract UsStock(string symbol, string primaryExchange = "NASDAQ") =>
        new(symbol, "STK", "SMART", "USD", primaryExchange);
}
