namespace TradingTerminal.Core.Backtest;

/// <summary>Sample of the equity curve at a point in time. <see cref="Equity"/> is mark-to-market.</summary>
public sealed record EquityPoint(DateTime TimestampUtc, double Equity);
