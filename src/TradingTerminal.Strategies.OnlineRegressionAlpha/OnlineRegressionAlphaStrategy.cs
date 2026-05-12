using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OnlineRegressionAlpha;

public sealed class OnlineRegressionAlphaStrategy : ITradingStrategy
{
    public string Id => "online.regression.alpha";
    public string DisplayName => "Online-regression alpha (RLS)";
    public string Description => "Recursive least squares with forgetting on (microprice dev, queue imbalance, rolling vol). First ML-driven strategy.";
}