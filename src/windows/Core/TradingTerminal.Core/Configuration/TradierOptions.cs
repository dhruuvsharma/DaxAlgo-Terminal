namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Tradier configuration — US equities and options.
///
/// <para>Sandbox and production are different hosts with different tokens, and a sandbox token is free
/// and issued immediately without a funded account. That makes this one of the fastest brokers on the
/// list to actually verify, which is why the default points at the sandbox.</para>
///
/// <para>The token itself is never here — it lives in the DPAPI credential store like every other
/// broker secret.</para>
/// </summary>
public sealed class TradierOptions
{
    public const string SectionName = "Tradier";

    /// <summary>True for the free sandbox. Different host, different token: a sandbox token against
    /// production is refused exactly like an invalid one.</summary>
    public bool Sandbox { get; set; } = true;

    public string SandboxBaseUrl { get; set; } = "https://sandbox.tradier.com";

    public string ProductionBaseUrl { get; set; } = "https://api.tradier.com";

    /// <summary>Symbols to offer. Plain US tickers.</summary>
    public string[] Instruments { get; set; } = ["AAPL", "MSFT", "SPY", "QQQ", "NVDA"];

    /// <summary>How often the quote endpoint is polled, in seconds.
    ///
    /// <para>Tradier's streaming needs a session token obtained separately; polling quotes is the
    /// honest first implementation, and saying how often it polls is better than a chart that updates
    /// at a rate nobody declared.</para>
    /// </summary>
    public int QuotePollSeconds { get; set; } = 2;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>The host for the configured environment.</summary>
    public string BaseUrl => Sandbox ? SandboxBaseUrl : ProductionBaseUrl;
}
