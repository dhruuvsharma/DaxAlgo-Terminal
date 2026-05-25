namespace TradingTerminal.UI;

/// <summary>
/// One row in a strategy's in-window data log. The collection these flow into is bounded —
/// oldest entries are dropped as new ones arrive, so the log is self-trimming and doesn't grow
/// without bound while a strategy runs.
/// </summary>
/// <param name="TimestampUtc">When the entry was emitted, UTC.</param>
/// <param name="Category">Short tag — BAR / SIGNAL / STREAM / ALGO / STRATEGY. Drives the
/// row's accent colour in the UI.</param>
/// <param name="Message">Free-form payload. Kept on one line — multi-line messages should be
/// split into multiple entries.</param>
public sealed record StrategyDataLogEntry(
    DateTime TimestampUtc,
    string Category,
    string Message);
