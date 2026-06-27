namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the NinjaTrader 8 backend. NinjaTrader is itself the host: this app
/// talks to the running NT instance through NTDirect.dll (P/Invoke), so the only
/// "connection" knobs are the account name and where the DLL lives.
/// </summary>
public sealed class NinjaTraderOptions
{
    public const string SectionName = "NinjaTrader";

    /// <summary>NinjaTrader account name (e.g. "Sim101" for the bundled simulation account).</summary>
    public string AccountName { get; set; } = "Sim101";

    /// <summary>
    /// Optional override path to NTDirect.dll. When empty the runtime checks the standard
    /// install path (<c>%USERPROFILE%\Documents\NinjaTrader 8\bin64\NTDirect.dll</c>). The
    /// MSBuild props in <c>TradingTerminal.Infrastructure.csproj</c> additionally probe this
    /// at build time and define <c>HAS_NTAPI</c> when the DLL is present.
    /// </summary>
    public string DllPath { get; set; } = string.Empty;

    /// <summary>
    /// NinjaTrader uses contract-month suffixes for futures (e.g. "ES 06-26"). When set, this
    /// suffix is appended to <c>Contract.Symbol</c> for futures lookups. Stocks/forex ignore it.
    /// </summary>
    public string DefaultFuturesContractMonth { get; set; } = string.Empty;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
