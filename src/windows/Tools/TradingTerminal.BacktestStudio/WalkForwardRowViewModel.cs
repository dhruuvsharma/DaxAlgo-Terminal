using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>A walk-forward fold flattened for the results grid: the in-sample-chosen parameters and
/// how they did on the following out-of-sample window.</summary>
public sealed class WalkForwardRowViewModel
{
    public WalkForwardRowViewModel(WalkForwardFold fold)
    {
        InSampleScore = fold.InSampleScore;
        OutOfSampleScore = fold.OutOfSampleScore;
        OutOfSampleNetProfit = fold.OutOfSampleNetProfit;
        OutOfSampleTrades = fold.OutOfSampleTradeCount;
        Window = $"IS {fold.InSampleFromUtc:MM-dd HH:mm}→{fold.InSampleToUtc:HH:mm}  ·  OOS →{fold.OutOfSampleToUtc:HH:mm}";
        Parameters = string.Join("   ", fold.BestParameters.Select(kv => $"{kv.Key}={kv.Value:G4}"));
    }

    public string Window { get; }
    public double InSampleScore { get; }
    public double OutOfSampleScore { get; }
    public double OutOfSampleNetProfit { get; }
    public int OutOfSampleTrades { get; }
    public string Parameters { get; }
}
