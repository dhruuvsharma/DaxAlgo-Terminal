namespace TradingTerminal.Core.Domain;

/// <summary>
/// A single price level on one side of the order book. Sizes are in contracts / shares /
/// units depending on the instrument's convention; brokers report integers, so we keep it
/// long to avoid float-precision drift on aggregations.
/// </summary>
public sealed record DepthLevel(double Price, long Size);
