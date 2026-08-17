using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization;

/// <summary>
/// Walk-forward analysis: splits the dataset into <c>folds + 1</c> equal time chunks; for each fold it
/// grid-optimizes on chunk k (in-sample) and then runs the winning parameters on chunk k+1
/// (out-of-sample, never seen by the optimizer). The IS→OOS gap exposes overfitting that a single
/// in-sample sweep hides. Operates on a pre-materialized event list (sliced per window) so it is
/// deterministic and independent of the data source — the caller materializes from synthetic or store.
/// </summary>
public sealed class WalkForwardOptimizer
{
    private readonly IReadOnlyList<MarketEvent> _events;
    private readonly Func<IStrategyKernel> _kernelFactory;

    public WalkForwardOptimizer(IReadOnlyList<MarketEvent> events, Func<IStrategyKernel> kernelFactory)
    {
        _events = events;
        _kernelFactory = kernelFactory;
    }

    public async Task<WalkForwardResult> RunAsync(OptimizationSpec spec, int folds, CancellationToken ct = default)
    {
        if (folds < 1) throw new ArgumentOutOfRangeException(nameof(folds));
        if (_events.Count == 0) return new WalkForwardResult(Array.Empty<WalkForwardFold>());

        var t0 = _events[0].TimestampUtc;
        var t1 = _events[^1].TimestampUtc;
        var chunk = (t1 - t0) / (folds + 1);
        if (chunk <= TimeSpan.Zero) return new WalkForwardResult(Array.Empty<WalkForwardFold>());

        var results = new List<WalkForwardFold>(folds);
        for (var k = 0; k < folds; k++)
        {
            var isFrom = t0 + chunk * k;
            var isTo = t0 + chunk * (k + 1);
            var oosFrom = isTo;
            var oosTo = k + 2 <= folds ? t0 + chunk * (k + 2) : t1 + TimeSpan.FromTicks(1); // last fold runs to the end inclusive

            var isEvents = Slice(isFrom, isTo);
            var oosEvents = Slice(oosFrom, oosTo);
            if (isEvents.Count == 0 || oosEvents.Count == 0) continue;

            // Optimize on the in-sample slice.
            var isSpec = spec with
            {
                BaseRun = spec.BaseRun with { Data = spec.BaseRun.Data with { FromUtc = isFrom, ToUtc = isTo } },
            };
            var isResult = await new GridOptimizer(() => new InMemoryMarketDataFeed(isEvents), _kernelFactory)
                .RunAsync(isSpec, progress: null, ct).ConfigureAwait(false);
            if (isResult.Best is not { } best) continue;

            // Apply the winners to the out-of-sample slice.
            var oosTrial = await TrialRunner.EvaluateAsync(
                () => new InMemoryMarketDataFeed(oosEvents), _kernelFactory,
                spec.BaseRun, spec.Criterion, best.Parameters, ct).ConfigureAwait(false);

            results.Add(new WalkForwardFold(
                isFrom, isTo, oosFrom, oosTo,
                best.Parameters,
                best.Score,
                oosTrial.Score,
                oosTrial.NetProfit,
                oosTrial.TradeCount));
        }

        return new WalkForwardResult(results);
    }

    private List<MarketEvent> Slice(DateTime fromUtc, DateTime toUtc)
    {
        var slice = new List<MarketEvent>();
        foreach (var e in _events)
            if (e.TimestampUtc >= fromUtc && e.TimestampUtc < toUtc) slice.Add(e);
        return slice;
    }
}
