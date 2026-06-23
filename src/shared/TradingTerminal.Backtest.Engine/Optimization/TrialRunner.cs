using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization;

/// <summary>Runs one parameter combination through the engine and scores it — the shared unit of work
/// for every optimizer (grid, genetic, walk-forward). Overlays the combo onto the base run's
/// parameters and forces visual recording off so a sweep never pays the timeline cost.</summary>
internal static class TrialRunner
{
    public static async Task<OptimizationTrial> EvaluateAsync(
        Func<IMarketDataFeed> feedFactory,
        Func<IStrategyKernel> kernelFactory,
        RunSpec baseRun,
        OptimizationCriterion criterion,
        IReadOnlyDictionary<string, double> combo,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, double>(baseRun.ParametersOrEmpty.Values);
        foreach (var (k, v) in combo) merged[k] = v;

        var spec = baseRun with { Parameters = new StrategyParameters(merged), Visual = VisualRecording.Off };
        var report = await new BacktestEngine(feedFactory()).RunAsync(spec, kernelFactory(), ct).ConfigureAwait(false);

        return new OptimizationTrial(
            combo,
            Criteria.Score(criterion, report),
            report.Summary.NetProfit,
            report.Trades.Count);
    }
}
