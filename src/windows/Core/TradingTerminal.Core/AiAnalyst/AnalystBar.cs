namespace TradingTerminal.Core.AiAnalyst;

/// <summary>
/// Wire-format OHLCV bar handed to the Python analyst. Mirrors <c>Bar</c> but lives in the
/// AiAnalyst namespace so the JSON contract is decoupled from market-data internals.
/// </summary>
public sealed record AnalystBar(
    DateTime TimestampUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume);
