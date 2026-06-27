namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Kraken backend. Public market data needs no API key and no account: the
/// WebSocket v2 carries live bars (ohlc) / L1 (ticker) / L2 (book) / trades, and the REST OHLC
/// endpoint serves history. Symbols use Kraken's slash form (e.g. <c>BTC/USD</c>); REST history maps
/// these to Kraken's altname pairs (XBT etc.) inside the client.
/// </summary>
public sealed class KrakenOptions
{
    public const string SectionName = "Kraken";

    /// <summary>REST base for historical OHLC + the connectivity check. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://api.kraken.com";

    /// <summary>WebSocket v2 public endpoint.</summary>
    public string WsBaseUrl { get; set; } = "wss://ws.kraken.com/v2";

    /// <summary>Curated symbols for the picker, in Kraken slash form. Subscriptions accept any valid pair.</summary>
    public string[] Instruments { get; set; } =
    [
        "BTC/USD", "ETH/USD", "SOL/USD", "XRP/USD", "ADA/USD",
        "DOGE/USD", "AVAX/USD", "LINK/USD", "LTC/USD", "DOT/USD",
    ];

    /// <summary>Fractional crypto sizes scaled to the integer canonical size fields (see Binance notes).</summary>
    public double SizeScale { get; set; } = 1000.0;

    /// <summary>Order-book depth requested (Kraken supports 10/25/100/500/1000); 10 is a good default.</summary>
    public int DepthLevels { get; set; } = 10;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
