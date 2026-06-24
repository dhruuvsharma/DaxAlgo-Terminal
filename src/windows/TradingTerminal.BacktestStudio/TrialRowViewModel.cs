using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>A flattened optimization trial for the results grid — the parameter dictionary rendered
/// as a readable string alongside the headline numbers.</summary>
public sealed class TrialRowViewModel
{
    public TrialRowViewModel(OptimizationTrial trial)
    {
        Score = trial.Score;
        NetProfit = trial.NetProfit;
        TradeCount = trial.TradeCount;
        Parameters = string.Join("   ", trial.Parameters.Select(kv => $"{kv.Key}={kv.Value:G4}"));
    }

    public double Score { get; }
    public double NetProfit { get; }
    public int TradeCount { get; }
    public string Parameters { get; }
}
