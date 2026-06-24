using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications;

/// <summary>
/// Default <see cref="ISignalGate"/> — vetoes nothing. Registered by the notifications module so
/// the dispatcher always resolves a gate; the market-regime module registers a real gate that
/// supersedes this when the feature is wired up.
/// </summary>
internal sealed class AllowAllSignalGate : ISignalGate
{
    public bool ShouldSuppress(StrategyNotification notification, out string? reason)
    {
        reason = null;
        return false;
    }
}
