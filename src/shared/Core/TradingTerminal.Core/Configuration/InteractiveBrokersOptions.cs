namespace TradingTerminal.Core.Configuration;

public sealed class InteractiveBrokersOptions
{
    public const string SectionName = "InteractiveBrokers";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 1;
    public string AccountType { get; set; } = "Paper";

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>
    /// IB market-data subscription mode applied to all <c>reqMktData</c> requests.
    /// 1 = Live (default; requires a paid market-data subscription on the contract),
    /// 2 = Frozen (last known live tick when subscribed),
    /// 3 = Delayed (free, ~15 min lag),
    /// 4 = Delayed-Frozen (free, last known delayed tick — recommended fallback).
    /// Set to 3 or 4 if you see IB error 10089 ("requires additional subscription").
    /// </summary>
    public int MarketDataType { get; set; } = 1;
}
