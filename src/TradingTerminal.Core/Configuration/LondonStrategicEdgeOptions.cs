namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the London Strategic Edge backend — free multi-asset market data (no SDK, no
/// order path at the provider). One API key drives both seams:
/// <list type="bullet">
/// <item>Live L1 ticks over a single WebSocket (<see cref="WsUrl"/>): authenticate with
/// <c>{"action":"auth","api_key":...}</c>, then <c>{"action":"subscribe","symbol":...}</c> per
/// instrument; keepalive ping every <see cref="PingIntervalSeconds"/> (server idle timeout 600s).</item>
/// <item>Historical OHLCV over a PostgREST-style REST API (<see cref="RestBaseUrl"/>): key in the
/// <c>x-api-key</c> header, 5,000 rows/call, 100 calls/min.</item>
/// </list>
/// The instrument universe is published keyless at <see cref="CatalogUrl"/>. The free tier is a
/// 50 GB/month payload quota shared between streaming and download; <c>QUOTA_EXCEEDED</c> is a
/// fatal (non-reconnect) error. Docs: https://londonstrategicedge.com/api.
/// </summary>
public sealed class LondonStrategicEdgeOptions
{
    public const string SectionName = "LondonStrategicEdge";

    /// <summary>API key from londonstrategicedge.com/websockets (format <c>lse_live_…</c>).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>WebSocket endpoint for live ticks.</summary>
    public string WsUrl { get; set; } = "wss://data-ws.londonstrategicedge.com";

    /// <summary>PostgREST-style REST base for historical data. No trailing slash.</summary>
    public string RestBaseUrl { get; set; } = "https://api.londonstrategicedge.com/iso";

    /// <summary>Keyless instrument catalog (symbol / name / category entries).</summary>
    public string CatalogUrl { get; set; } = "https://londonstrategicedge.com/feed-catalog.json";

    /// <summary>Keepalive cadence — the provider's SDK pings every 25s against a 600s idle timeout.</summary>
    public int PingIntervalSeconds { get; set; } = 25;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
