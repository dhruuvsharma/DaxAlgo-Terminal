namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Deribit configuration — the venue where crypto options actually trade.
///
/// <para>Market data is public, so the defaults work with no account. Keys buy a higher rate-limit
/// budget and the private channels, and go in <see cref="Credentials"/>.</para>
/// </summary>
public sealed class DeribitOptions
{
    public const string SectionName = "Deribit";

    /// <summary>JSON-RPC over HTTP.</summary>
    public string RestBaseUrl { get; set; } = "https://www.deribit.com/api/v2";

    /// <summary>The same JSON-RPC, over a socket.</summary>
    public string WsBaseUrl { get; set; } = "wss://www.deribit.com/ws/api/v2";

    /// <summary>
    /// Instruments to offer.
    ///
    /// <para>Perpetuals by default rather than options: an option chain is hundreds of instruments and
    /// a picker full of strikes is a picker nobody can use. An author who wants a specific expiry names
    /// it here.</para>
    /// </summary>
    public string[] Instruments { get; set; } = ["BTC-PERPETUAL", "ETH-PERPETUAL"];

    /// <summary>
    /// How often the venue is asked to push. Deribit offers <c>100ms</c>, <c>raw</c>, and a slower
    /// aggregate; <c>100ms</c> is the sane default — <c>raw</c> is every book change, which is far more
    /// than a chart can draw and enough to spend the frame budget on its own.
    /// </summary>
    public string Interval { get; set; } = "100ms";

    /// <summary>Book levels to publish.</summary>
    public int DepthLevels { get; set; } = 10;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>API credentials, when the user chose the keyed way in. Empty means keyless, which is a
    /// fully supported mode here — public market data needs no account.</summary>
    public CryptoApiCredentials Credentials { get; set; } = new();
}
