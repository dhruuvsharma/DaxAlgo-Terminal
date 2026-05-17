namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Alpaca backend. Unlike IB / NT (local) and cTrader (TLS + protobuf to
/// Spotware), Alpaca is a REST + WebSocket service against api.alpaca.markets (live) or
/// paper-api.alpaca.markets (paper). Auth is a static API key + secret pair issued in the
/// Alpaca dashboard — no OAuth dance.
///
/// Generate keys at https://app.alpaca.markets (paper) or https://app.alpaca.markets/live
/// (funded). The same key works for trading, stock data, crypto data, and the streaming
/// endpoints; routing across asset classes is done by the client by Contract.SecType.
/// </summary>
public sealed class AlpacaOptions
{
    public const string SectionName = "Alpaca";

    /// <summary>API key id (shown on the dashboard, prefixed with "PK" for paper or "AK" for live).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>API secret (shown once on key creation; user must re-mint if lost).</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>True targets api.alpaca.markets (funded); false targets paper-api.alpaca.markets.</summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// Stock data feed. "iex" is free; "sip" requires a paid market-data subscription.
    /// Crypto and options ignore this — they have their own consolidated feeds.
    /// </summary>
    public string StockDataFeed { get; set; } = "iex";

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
