namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Coinbase backend. Like Binance, Coinbase market data is served from public
/// endpoints that need no API key and no account: the Advanced Trade WebSocket for live
/// bars / L1 / L2 / trades, and the Exchange REST API for historical candles. Symbols use Coinbase's
/// dash form (e.g. <c>BTC-USD</c>).
/// </summary>
public sealed class CoinbaseOptions
{
    public const string SectionName = "Coinbase";

    /// <summary>REST base for historical candles + the connectivity check. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://api.exchange.coinbase.com";

    /// <summary>Advanced Trade public market-data WebSocket.</summary>
    public string WsBaseUrl { get; set; } = "wss://advanced-trade-ws.coinbase.com";

    /// <summary>Curated symbols for the picker, in Coinbase dash form. Subscriptions accept any valid product id.</summary>
    public string[] Instruments { get; set; } =
    [
        "BTC-USD", "ETH-USD", "SOL-USD", "XRP-USD", "ADA-USD",
        "DOGE-USD", "AVAX-USD", "LINK-USD", "LTC-USD", "DOT-USD",
    ];

    /// <summary>Fractional crypto sizes scaled to the integer canonical size fields (see Binance notes).</summary>
    public double SizeScale { get; set; } = 1000.0;

    /// <summary>Order-book depth (levels) maintained from the level2 snapshot+update stream.</summary>
    public int DepthLevels { get; set; } = 20;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
