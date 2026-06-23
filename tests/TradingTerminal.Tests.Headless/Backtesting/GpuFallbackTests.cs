using FluentAssertions;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Backtest.Engine.Optimization.Gpu;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Covers the C# side of the GPU accelerator: the support gate and the guaranteed CPU
/// fallback when the CUDA binary isn't present (the .cu itself is built + validated on a CUDA box).</summary>
public sealed class GpuFallbackTests
{
    private static readonly InstrumentId Id = new(1);

    private static RunSpec BaseRun(string strategyId = "meanReversion") => new(
        Universe.Single(new InstrumentSpec(Id, Contract.UsStock("SYN"), 0.01, 1.0)),
        new DataSpec(), strategyId);

    private static OptimizationSpec Spec(OptimizationCriterion criterion = OptimizationCriterion.NetProfit, string strategyId = "meanReversion") => new(
        BaseRun(strategyId),
        new[] { ParameterAxis.Of("lookback", 10, 20, 30), ParameterAxis.Of("entryZ", 1.0, 2.0) },
        criterion);

    [Fact]
    public void Support_gate_accepts_meanreversion_netprofit_and_rejects_the_rest()
    {
        ProcessGpuOptimizer.Supports(Spec()).Should().BeTrue();
        ProcessGpuOptimizer.Supports(Spec(OptimizationCriterion.Sharpe)).Should().BeFalse();
        ProcessGpuOptimizer.Supports(Spec(strategyId: "somethingElse")).Should().BeFalse();

        var unsupportedAxis = new OptimizationSpec(
            BaseRun(), new[] { ParameterAxis.Of("qty", 1, 2) }, OptimizationCriterion.NetProfit);
        ProcessGpuOptimizer.Supports(unsupportedAxis).Should().BeFalse();
    }

    [Fact]
    public void Missing_binary_is_not_available()
    {
        new ProcessGpuOptimizer(@"Z:\does\not\exist\gpu_optimizer.exe").IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Hybrid_optimizer_falls_back_to_cpu_when_gpu_absent()
    {
        var gpu = new ProcessGpuOptimizer(@"Z:\does\not\exist\gpu_optimizer.exe");
        var hybrid = new HybridGridOptimizer(
            gpu,
            () => new SyntheticMarketDataFeed(Id, 3000, seed: 11),
            () => new MeanReversionKernel());

        hybrid.WillUseGpu(Spec()).Should().BeFalse();

        var (result, usedGpu) = await hybrid.RunAsync(Spec());

        usedGpu.Should().BeFalse();
        result.Evaluations.Should().Be(6); // 3 lookback x 2 entryZ on the CPU optimizer
        result.Best.Should().NotBeNull();
    }
}
