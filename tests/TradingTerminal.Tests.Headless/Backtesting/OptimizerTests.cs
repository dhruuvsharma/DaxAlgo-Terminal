using FluentAssertions;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Covers the grid optimizer: Cartesian expansion, that it evaluates every combination,
/// ranks best-first by the criterion, and is deterministic for a fixed seed.</summary>
public sealed class OptimizerTests
{
    private static readonly InstrumentId Id = new(1);

    private static OptimizationSpec Spec() => new(
        BaseRun: new RunSpec(
            Universe.Single(new InstrumentSpec(Id, Contract.UsStock("SYN"), 0.01, 1.0)),
            new DataSpec(),
            StrategyId: "meanReversion"),
        Axes: new[]
        {
            ParameterAxis.Of("lookback", 10, 20, 30),
            ParameterAxis.Of("entryZ", 1.0, 1.5, 2.0),
        },
        Criterion: OptimizationCriterion.NetProfit);

    private static GridOptimizer Optimizer() => new(
        feedFactory: () => new SyntheticMarketDataFeed(Id, 3000, seed: 7),
        kernelFactory: () => new MeanReversionKernel());

    [Fact]
    public void Expand_takes_the_cartesian_product_of_axes()
    {
        var combos = GridOptimizer.Expand(new[]
        {
            ParameterAxis.Of("a", 1, 2),
            ParameterAxis.Of("b", 10, 20, 30),
        });

        combos.Should().HaveCount(6);
        combos.Should().OnlyContain(c => c.ContainsKey("a") && c.ContainsKey("b"));
    }

    [Fact]
    public void Range_axis_is_inclusive_of_the_endpoint()
    {
        ParameterAxis.Range("x", 1, 3, 1).Values.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Optimizer_evaluates_every_combo_and_ranks_best_first()
    {
        var progressSeen = new List<int>();
        var result = await Optimizer().RunAsync(Spec(), new Progress<int>(progressSeen.Add));

        result.Evaluations.Should().Be(9); // 3 lookback x 3 entryZ
        result.Best.Should().NotBeNull();
        result.Trials.Should().BeInDescendingOrder(t => t.Score);
        result.Best!.Score.Should().Be(result.Trials.Max(t => t.Score));
        result.Best.Parameters.Should().ContainKeys("lookback", "entryZ");
    }

    [Fact]
    public async Task Optimizer_is_deterministic_for_a_fixed_seed()
    {
        var a = await Optimizer().RunAsync(Spec());
        var b = await Optimizer().RunAsync(Spec());

        a.Best!.Score.Should().Be(b.Best!.Score);
        a.Best.Parameters.Should().BeEquivalentTo(b.Best.Parameters);
    }
}
