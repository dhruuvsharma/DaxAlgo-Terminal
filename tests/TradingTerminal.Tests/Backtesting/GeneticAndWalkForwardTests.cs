using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Optimization;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Covers the genetic optimizer and walk-forward analysis built on the new engine.</summary>
public sealed class GeneticAndWalkForwardTests
{
    private static readonly InstrumentId Id = new(1);

    private static RunSpec BaseRun() => new(
        Universe.Single(new InstrumentSpec(Id, Contract.UsStock("SYN"), 0.01, 1.0)),
        new DataSpec(),
        StrategyId: "meanReversion");

    private static OptimizationSpec Spec() => new(
        BaseRun(),
        new[]
        {
            ParameterAxis.Range("lookback", 10, 40, 5),
            ParameterAxis.Range("entryZ", 1.0, 2.5, 0.5),
        },
        OptimizationCriterion.NetProfit);

    private static async Task<List<MarketEvent>> SyntheticEventsAsync(int count, int seed)
    {
        var feed = new SyntheticMarketDataFeed(Id, count, seed);
        var list = new List<MarketEvent>(count);
        await foreach (var ev in feed.StreamAsync(BaseRun(), CancellationToken.None)) list.Add(ev);
        return list;
    }

    [Fact]
    public async Task Genetic_optimizer_finds_a_best_and_is_seed_deterministic()
    {
        GeneticOptimizer Make() => new(
            () => new SyntheticMarketDataFeed(Id, 3000, seed: 5),
            () => new MeanReversionKernel());
        var options = new GeneticOptions(PopulationSize: 16, Generations: 6, Seed: 42);

        var a = await Make().RunAsync(Spec(), options);
        var b = await Make().RunAsync(Spec(), options);

        a.Best.Should().NotBeNull();
        a.Trials.Should().BeInDescendingOrder(t => t.Score);
        // Genetic explores a subset of the 7x4=28 grid points, never more than the whole space.
        a.Evaluations.Should().BeLessThanOrEqualTo(28);
        a.Best!.Score.Should().Be(b.Best!.Score); // deterministic for a fixed seed
        a.Best.Parameters.Should().BeEquivalentTo(b.Best.Parameters);
    }

    [Fact]
    public async Task Walk_forward_produces_one_fold_per_split_with_out_of_sample_results()
    {
        var events = await SyntheticEventsAsync(4000, seed: 9);
        var wf = new WalkForwardOptimizer(events, () => new MeanReversionKernel());

        var result = await wf.RunAsync(Spec(), folds: 3);

        result.Folds.Should().HaveCount(3);
        result.Folds.Should().OnlyContain(f => f.OutOfSampleFromUtc >= f.InSampleToUtc); // OOS follows IS
        result.Folds.Should().OnlyContain(f => f.BestParameters.ContainsKey("lookback"));
        result.Folds.Select(f => f.InSampleFromUtc).Should().BeInAscendingOrder(); // folds walk forward in time
    }
}
