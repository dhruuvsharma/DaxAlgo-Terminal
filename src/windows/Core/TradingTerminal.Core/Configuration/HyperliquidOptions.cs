namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Hyperliquid configuration — the perpetuals DEX.
///
/// <para>Market data is public and needs no account. Keys exist for trading, not for reading, so the
/// keyless row is the whole story for a chart.</para>
/// </summary>
public sealed class HyperliquidOptions
{
    public const string SectionName = "Hyperliquid";

    /// <summary>
    /// The info endpoint. Everything read from this venue is a <b>POST</b> to one URL with a
    /// <c>type</c> in the body — there are no per-resource paths and no query strings.
    /// </summary>
    public string RestBaseUrl { get; set; } = "https://api.hyperliquid.xyz";

    public string WsBaseUrl { get; set; } = "wss://api.hyperliquid.xyz/ws";

    /// <summary>Coins to offer. Hyperliquid names a perpetual by its base asset alone — <c>BTC</c>,
    /// not <c>BTC-PERP</c>.</summary>
    public string[] Instruments { get; set; } = ["BTC", "ETH", "SOL", "HYPE"];

    /// <summary>Sizes are fractional base quantities, so they are scaled to whole units for the
    /// terminal's integer size fields. Ten thousand keeps four decimals of a coin.</summary>
    public double SizeScale { get; set; } = 10_000d;

    public int DepthLevels { get; set; } = 10;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
