namespace TradingTerminal.Core.Strategies;

/// <summary>A strategy's direction-only signal, independent of order execution.</summary>
public enum StrategySignalKind : long
{
    Short = -1,
    Flat = 0,
    Long = 1,
}

/// <summary>
/// One ordered strategy signal. <paramref name="Strength"/> is normalized to <c>[0,1]</c>;
/// <paramref name="NoteId"/> is a strategy-defined non-negative numeric note identifier.
/// </summary>
public readonly record struct StrategySignal(
    StrategySignalKind Kind,
    double Strength,
    long NoteId = 0);

/// <summary>One strategy signal stamped by the host clock that received it.</summary>
public readonly record struct StrategySignalEvent(
    DateTime TimestampUtc,
    StrategySignal Signal);

/// <summary>
/// Optional capability exposed by a strategy host that can render direction-only signals without
/// coercing them into orders. Strategies continue to receive <c>IOrderRouter</c>; protected or
/// signal-native strategies use this capability when the supplied router implements it.
/// </summary>
public interface IStrategySignalSink
{
    Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default);
}
