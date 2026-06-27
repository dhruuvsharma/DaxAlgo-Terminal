namespace TradingTerminal.Core.Notifications;

/// <summary>
/// One unit of "something a strategy wants the user to know about." Transports format and
/// dispatch these; the strategy never knows which transports are wired up.
/// </summary>
public sealed record StrategyNotification(
    NotificationKind Kind,
    string StrategyId,
    string StrategyName,
    string Symbol,
    string? Direction,
    string Message,
    DateTime TimestampUtc)
{
    public static StrategyNotification Test() => new(
        Kind: NotificationKind.Test,
        StrategyId: "settings.test",
        StrategyName: "Settings",
        Symbol: "—",
        Direction: null,
        Message: "Test notification from DaxAlgo Terminal.",
        TimestampUtc: DateTime.UtcNow);
}
