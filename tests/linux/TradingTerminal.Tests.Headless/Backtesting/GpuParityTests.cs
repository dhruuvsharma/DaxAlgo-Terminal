using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Backtest.Engine.Optimization.Gpu;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Validates the CUDA accelerator against the CPU optimizer: the GPU's net profit per combo
/// must match <see cref="GridOptimizer"/> to floating point on identical data. Soft-skips when the
/// gpu_optimizer binary hasn't been built (CI without CUDA stays green).</summary>
public sealed class GpuParityTests
{
    private static readonly InstrumentId Id = new(1);

    private static string GpuExe([CallerFilePath] string? thisFile = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "..",
            "tools", "cpp-backtester", "gpu", "build", "gpu_optimizer.exe"));

    [Fact]
    public async Task Gpu_net_profit_matches_cpu_optimizer()
    {
        var exe = GpuExe();
        var gpu = new ProcessGpuOptimizer(exe);
        if (!gpu.IsAvailable) return; // soft-skip: GPU binary not built on this machine

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = new List<MarketEvent>(1500);
        var quotes = new List<(double Bid, double Ask)>(1500);
        for (var i = 0; i < 1500; i++)
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0);
            double bid = mid - 0.01, ask = mid + 0.01;
            events.Add(MarketEvent.OfQuote(Id, new Tick(start.AddSeconds(i), bid, ask, 10, 10)));
            quotes.Add((bid, ask));
        }

        var baseRun = new RunSpec(
            Universe.Single(new InstrumentSpec(Id, Contract.UsStock("SYN"), 0.01, 1.0)),
            new DataSpec(), "meanReversion",
            new StrategyParameters(new Dictionary<string, double> { ["exitZ"] = 0.5, ["qty"] = 5 }));
        var spec = new OptimizationSpec(
            baseRun,
            new[] { ParameterAxis.Of("lookback", 10, 20, 30), ParameterAxis.Of("entryZ", 1.0, 1.5, 2.0) },
            OptimizationCriterion.NetProfit);

        var cpu = await new GridOptimizer(() => new InMemoryMarketDataFeed(events), () => new MeanReversionKernel())
            .RunAsync(spec);
        var gpuResult = await gpu.RunAsync(spec, quotes);

        gpuResult.Trials.Should().HaveCount(cpu.Trials.Count);
        foreach (var g in gpuResult.Trials)
        {
            var cpuMatch = cpu.Trials.Single(c =>
                (int)Math.Round(c.Parameters["lookback"]) == (int)Math.Round(g.Parameters["lookback"]) &&
                Math.Abs(c.Parameters["entryZ"] - g.Parameters["entryZ"]) < 1e-9);
            g.NetProfit.Should().BeApproximately(cpuMatch.NetProfit, 1e-6);
            g.TradeCount.Should().Be(cpuMatch.TradeCount);
        }
    }
}
