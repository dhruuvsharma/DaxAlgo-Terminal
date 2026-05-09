namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the cTrader Open API 2.0 backend. Unlike NinjaTrader (local) and IB
/// (local TWS socket), cTrader connects to Spotware's hosted endpoint over TLS+protobuf.
///
/// To get the values below, register an application at
/// https://connect.spotware.com/apps and run the OAuth flow to obtain an access token
/// and the trading-account ctidTraderAccountId you want to drive.
/// </summary>
public sealed class CTraderOptions
{
    public const string SectionName = "CTrader";

    public string Host { get; set; } = "demo.ctraderapi.com";
    public int Port { get; set; } = 5035;

    /// <summary>OAuth application clientId (from connect.spotware.com/apps).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth application clientSecret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth access token tied to the trading account.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>The numeric ctidTraderAccountId of the cTrader trading account to drive.</summary>
    public long CtidTraderAccountId { get; set; }

    /// <summary>True when targeting the live endpoint (live.ctraderapi.com), false for demo.</summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// When true the real cTrader client is required. When false the synthetic fallback is used.
    /// </summary>
    public bool UseRealClient { get; set; }

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
