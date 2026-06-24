using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Regime;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Suppresses strategy <see cref="NotificationKind.Signal"/> alerts while the market is
/// risk-off (composite at/below <see cref="MarketRegimeOptions.RiskOffThreshold"/>), when
/// <see cref="MarketRegimeOptions.GateSignalsWhenRiskOff"/> is enabled. The signal still lands
/// in the strategy's own Signals list — only the outbound notification is vetoed, so the user
/// isn't pinged to chase longs into a falling tape. Non-signal kinds (regime changes, arm/stop,
/// test) always pass.
/// </summary>
internal sealed class RegimeSignalGate : ISignalGate
{
    private readonly IMarketRegimeProvider _provider;
    private readonly IOptionsMonitor<MarketRegimeOptions> _options;

    public RegimeSignalGate(IMarketRegimeProvider provider, IOptionsMonitor<MarketRegimeOptions> options)
    {
        _provider = provider;
        _options = options;
    }

    public bool ShouldSuppress(StrategyNotification notification, out string? reason)
    {
        reason = null;
        var o = _options.CurrentValue;
        if (!o.GateSignalsWhenRiskOff || notification.Kind != NotificationKind.Signal)
            return false;

        var snapshot = _provider.Current;
        if (snapshot.Unavailable) return false;

        if (snapshot.CompositeScore <= o.RiskOffThreshold)
        {
            reason = $"regime risk-off ({snapshot.CompositeScore:F0} ≤ {o.RiskOffThreshold:F0}, {snapshot.Label})";
            return true;
        }
        return false;
    }
}
