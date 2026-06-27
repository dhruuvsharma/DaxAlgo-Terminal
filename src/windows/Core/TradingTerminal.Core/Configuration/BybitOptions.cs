namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Bybit backend. Public market data needs no API key and no account: the v5 public
/// WebSocket carries live bars (kline) / L1 (tickers) / L2 (orderbook) / trades (publicTrade), and the
/// v5 REST market endpoint serves historical kline. Symbols use Bybit's joined form (e.g. <c>BTCUSDT</c>).
/// </summary>
public sealed class BybitOptions
{
    public const string SectionName = "Bybit";

    /// <summary>REST base for historical kline + the connectivity check. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://api.bybit.com";

    /// <summary>v5 public spot WebSocket.</summary>
    public string WsBaseUrl { get; set; } = "wss://stream.bybit.com/v5/public/spot";

    /// <summary>Product category for REST kline (spot / linear / inverse). Spot by default.</summary>
    public string Category { get; set; } = "spot";

    /// <summary>Curated symbols for the picker, in Bybit joined form. Subscriptions accept any valid symbol.</summary>
    public string[] Instruments { get; set; } =
    [
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "ADAUSDT",
        "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT", "DOTUSDT",
    ];

    /// <summary>Fractional crypto sizes scaled to the integer canonical size fields (see Binance notes).</summary>
    public double SizeScale { get; set; } = 1000.0;

    /// <summary>Order-book depth requested (Bybit spot supports 1/50/200); 50 is a good default.</summary>
    public int DepthLevels { get; set; } = 50;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
