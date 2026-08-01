namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Which product edition an app shell ships as. Selected at composition time by the shell
/// executable (there is one <c>WinExe</c> per edition), not from configuration — a lower-tier
/// build simply never references or registers the higher-tier feature projects.
/// </summary>
/// <remarks>
/// Ordered least-to-most capable so <c>&gt;=</c> comparisons read naturally
/// (e.g. "credentialed brokers when <c>edition == Professional</c>").
/// </remarks>
public enum AppEdition
{
    /// <summary>Keyless brokers with the base strategy, data, and settings surfaces.</summary>
    Basic,

    /// <summary>The private overlay's higher-tier broker and feature composition.</summary>
    Professional,
}
