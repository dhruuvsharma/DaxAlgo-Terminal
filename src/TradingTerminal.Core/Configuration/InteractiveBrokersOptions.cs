namespace TradingTerminal.Core.Configuration;

public sealed class InteractiveBrokersOptions
{
    public const string SectionName = "InteractiveBrokers";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 1;
    public string AccountType { get; set; } = "Paper";

    /// <summary>When true the real TWS client is required (lib/IBApi.dll must be present).</summary>
    public bool UseRealClient { get; set; }

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
