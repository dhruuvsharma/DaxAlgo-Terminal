namespace TradingTerminal.Core.Trading;

/// <summary>
/// Lifecycle of a single order. <see cref="PendingNew"/> is the optimistic local state
/// before the broker acknowledges; <see cref="Working"/> means the broker accepted the
/// order (resting for limit/stop, in-flight for market). Terminal states:
/// <see cref="Filled"/>, <see cref="Cancelled"/>, <see cref="Rejected"/>.
/// </summary>
public enum OrderState
{
    PendingNew,
    Working,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
}
