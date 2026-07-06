namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Which product edition an app shell ships as. Selected at composition time by the shell
/// executable (there is one <c>WinExe</c> per edition), not from configuration — a lower-tier
/// build simply never references or registers the higher-tier feature projects.
/// </summary>
/// <remarks>
/// Ordered least-to-most capable so <c>&gt;=</c> comparisons read naturally
/// (e.g. "credentialed brokers when <c>edition &gt;= Intermediate</c>").
/// </remarks>
public enum AppEdition
{
    /// <summary>Keyless brokers only, core charts/tools/strategies. No ML / AI / LSE / QuantConnect / experimental.</summary>
    Basic,

    /// <summary>All brokers (credentialed login) with the same tool set as <see cref="Basic"/>.</summary>
    Intermediate,

    /// <summary>Everything — every broker, tool, ML / AI / LSE / QuantConnect / Paper Lab / experimental surface.</summary>
    Professional,
}
