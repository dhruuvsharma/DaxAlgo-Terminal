namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Binance backend. Unlike the account-based brokers, Binance market data is
/// served from <b>public</b> WebSocket + REST endpoints that need <b>no API key and no account</b>
/// — so it's the zero-credential way to run the terminal against a real, live feed. It carries
/// real bars, L1 (book ticker), L2 depth, and the trade tape for crypto pairs.
///
/// Defaults target the global market-data hosts. If those are geo-blocked where you run, point
/// <see cref="RestBaseUrl"/> / <see cref="WsBaseUrl"/> at the Binance.US hosts
/// (<c>https://api.binance.us</c> / <c>wss://stream.binance.us:9443</c>) or the data-only mirror
/// (<c>https://data-api.binance.vision</c> / <c>wss://data-stream.binance.vision</c>).
/// </summary>
public sealed class BinanceOptions
{
    public const string SectionName = "Binance";

    /// <summary>REST base for the ping/connectivity check and historical klines. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://api.binance.com";

    /// <summary>WebSocket base for the live streams (raw single-stream form, <c>/ws/&lt;stream&gt;</c>).</summary>
    public string WsBaseUrl { get; set; } = "wss://stream.binance.com:9443";

    /// <summary>
    /// Curated symbols surfaced by <c>ListInstrumentsAsync</c>, in Binance's native symbol form
    /// (e.g. <c>BTCUSDT</c>, no slash). The picker shows these; subscriptions accept any valid
    /// Binance symbol regardless of this list.
    /// </summary>
    public string[] Instruments { get; set; } =
    [
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT",
        "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "LTCUSDT",
    ];

    /// <summary>
    /// Crypto order/trade quantities are fractional (0.0123 BTC), but the canonical size fields are
    /// integers (<c>long</c>). We multiply every size — quote sizes, depth levels, trade prints, and
    /// bar volume — by this factor and round, so the size fields stay non-zero and comparable across
    /// the order book / footprint / volume views. Purely a relative-size scale, not a unit change.
    /// </summary>
    public double SizeScale { get; set; } = 1000.0;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
