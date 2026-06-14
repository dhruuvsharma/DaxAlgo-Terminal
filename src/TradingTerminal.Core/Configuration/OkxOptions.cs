namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the OKX backend. Public market data needs no API key and no account: the v5 public
/// WebSocket carries live bars (candle) / L1 (tickers) / L2 (books5 — full 5-level snapshots, no
/// reconstruction) / trades, and the v5 REST market endpoint serves historical candles. Symbols use
/// OKX's dash form (e.g. <c>BTC-USDT</c>).
/// </summary>
public sealed class OkxOptions
{
    public const string SectionName = "Okx";

    /// <summary>REST base for historical candles + the connectivity check. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://www.okx.com";

    /// <summary>v5 public WebSocket.</summary>
    public string WsBaseUrl { get; set; } = "wss://ws.okx.com:8443/ws/v5/public";

    /// <summary>Curated symbols for the picker, in OKX dash form. Subscriptions accept any valid instId.</summary>
    public string[] Instruments { get; set; } =
    [
        "BTC-USDT", "ETH-USDT", "SOL-USDT", "XRP-USDT", "ADA-USDT",
        "DOGE-USDT", "AVAX-USDT", "LINK-USDT", "LTC-USDT", "DOT-USDT",
    ];

    /// <summary>Fractional crypto sizes scaled to the integer canonical size fields (see Binance notes).</summary>
    public double SizeScale { get; set; } = 1000.0;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
