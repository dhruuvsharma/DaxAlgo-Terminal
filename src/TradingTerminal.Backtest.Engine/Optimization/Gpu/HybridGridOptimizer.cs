using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization.Gpu;

/// <summary>
/// Runs a grid sweep on the GPU when it's available and the spec is GPU-portable, otherwise on the CPU
/// <see cref="GridOptimizer"/> — and falls back to CPU if the GPU run fails for any reason. The CPU
/// path is the guaranteed contract; the GPU is a transparent accelerator. Returns whether the GPU was
/// actually used so the UI can surface it.
/// </summary>
public sealed class HybridGridOptimizer
{
    private readonly ProcessGpuOptimizer _gpu;
    private readonly Func<IMarketDataFeed> _feedFactory;
    private readonly Func<IStrategyKernel> _kernelFactory;

    public HybridGridOptimizer(
        ProcessGpuOptimizer gpu, Func<IMarketDataFeed> feedFactory, Func<IStrategyKernel> kernelFactory)
    {
        _gpu = gpu;
        _feedFactory = feedFactory;
        _kernelFactory = kernelFactory;
    }

    public bool WillUseGpu(OptimizationSpec spec) => _gpu.IsAvailable && ProcessGpuOptimizer.Supports(spec);

    public async Task<(OptimizationResult Result, bool UsedGpu)> RunAsync(
        OptimizationSpec spec, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (WillUseGpu(spec))
        {
            try
            {
                var quotes = await MaterializeQuotesAsync(_feedFactory(), spec, ct).ConfigureAwait(false);
                var result = await _gpu.RunAsync(spec, quotes, ct).ConfigureAwait(false);
                return (result, true);
            }
            catch (GpuUnavailableException)
            {
                // Fall through to the CPU optimizer — the guaranteed path.
            }
        }

        var cpu = await new GridOptimizer(_feedFactory, _kernelFactory).RunAsync(spec, progress, ct).ConfigureAwait(false);
        return (cpu, false);
    }

    private static async Task<List<(double Bid, double Ask)>> MaterializeQuotesAsync(
        IMarketDataFeed feed, OptimizationSpec spec, CancellationToken ct)
    {
        var quotes = new List<(double, double)>();
        await foreach (var ev in feed.StreamAsync(spec.BaseRun, ct).ConfigureAwait(false))
            if (ev.Kind == MarketEventKind.Quote && ev.Quote is { } q)
                quotes.Add((q.Bid, q.Ask));
        return quotes;
    }
}
