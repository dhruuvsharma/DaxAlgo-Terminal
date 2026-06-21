using System.Collections.Concurrent;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization;

/// <summary>
/// Exhaustive (grid) parameter optimizer: expands the Cartesian product of the axes and runs one
/// backtest per combination, in parallel across CPU cores. Each trial gets its own feed and kernel
/// instance (both stateful) from the supplied factories, so the parallel runs never share mutable
/// state. Results are ranked best-first by the criterion. This is the CPU baseline; the C++/GPU
/// accelerator will implement the same contract for GPU-portable kernels.
/// </summary>
public sealed class GridOptimizer
{
    private readonly Func<IMarketDataFeed> _feedFactory;
    private readonly Func<IStrategyKernel> _kernelFactory;

    public GridOptimizer(Func<IMarketDataFeed> feedFactory, Func<IStrategyKernel> kernelFactory)
    {
        _feedFactory = feedFactory;
        _kernelFactory = kernelFactory;
    }

    public async Task<OptimizationResult> RunAsync(
        OptimizationSpec spec, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var combos = Expand(spec.Axes);
        var dop = spec.MaxDegreeOfParallelism > 0 ? spec.MaxDegreeOfParallelism : Environment.ProcessorCount;

        var trials = new ConcurrentBag<OptimizationTrial>();
        var done = 0;

        await Parallel.ForEachAsync(
            combos,
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            async (combo, token) =>
            {
                var merged = new Dictionary<string, double>(spec.BaseRun.ParametersOrEmpty.Values);
                foreach (var (k, v) in combo) merged[k] = v;

                var runSpec = spec.BaseRun with
                {
                    Parameters = new StrategyParameters(merged),
                    Visual = VisualRecording.Off,
                };

                var report = await new BacktestEngine(_feedFactory())
                    .RunAsync(runSpec, _kernelFactory(), token).ConfigureAwait(false);

                trials.Add(new OptimizationTrial(
                    Parameters: combo,
                    Score: Criteria.Score(spec.Criterion, report),
                    NetProfit: report.Summary.NetProfit,
                    TradeCount: report.Trades.Count));

                progress?.Report(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);

        var ranked = trials.OrderByDescending(t => t.Score).ToList();
        return new OptimizationResult(spec.Criterion, ranked, ranked.FirstOrDefault());
    }

    /// <summary>Cartesian product of the axes into one dictionary per combination.</summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, double>> Expand(IReadOnlyList<ParameterAxis> axes)
    {
        var result = new List<IReadOnlyDictionary<string, double>> { new Dictionary<string, double>() };
        foreach (var axis in axes)
        {
            var next = new List<IReadOnlyDictionary<string, double>>(result.Count * Math.Max(1, axis.Values.Count));
            foreach (var partial in result)
                foreach (var value in axis.Values)
                    next.Add(new Dictionary<string, double>(partial) { [axis.Name] = value });
            result = next;
        }
        return result;
    }
}
