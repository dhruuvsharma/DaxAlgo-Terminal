using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.AnomalyDetector;

public sealed class AnomalyDetectorStrategy : ITradingStrategy
{
    public string Id => "anomaly.detector";
    public string DisplayName => "Rolling z-score anomaly detector";
    public string Description => "Spread / queue-imbalance / 1-tick-return z-scores. Risk filter + exchange-glitch detector.";
}