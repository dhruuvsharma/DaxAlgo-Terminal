namespace TradingTerminal.Core.Domain;

/// <summary>An OHLCV bar at a specific UTC timestamp (the bar's open time).</summary>
public sealed record Bar(
    DateTime TimestampUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume);
