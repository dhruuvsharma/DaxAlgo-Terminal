using FluentAssertions;
using TradingTerminal.Core.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

public sealed class MonteCarloTests
{
    [Fact]
    public void Run_AllPositiveTrades_ProducesHighProbabilityOfProfit()
    {
        var pnls = Enumerable.Repeat(10.0, 50).ToList();
        var r = MonteCarlo.Run(pnls, startingCash: 1000, simulations: 1000, seed: 42);
        r.ProbabilityOfProfit.Should().Be(1.0);
        r.FinalEquityPercentiles.Should().AllSatisfy(eq => eq.Should().Be(1500));
    }

    [Fact]
    public void Run_NegativeTrades_ProducesProbabilityOfProfitZero()
    {
        var pnls = Enumerable.Repeat(-5.0, 30).ToList();
        var r = MonteCarlo.Run(pnls, startingCash: 1000, simulations: 1000, seed: 7);
        r.ProbabilityOfProfit.Should().Be(0.0);
    }

    [Fact]
    public void Run_RandomTrades_PercentilesAreOrdered()
    {
        var rng = new Random(99);
        var pnls = Enumerable.Range(0, 100).Select(_ => (rng.NextDouble() - 0.45) * 20).ToList();
        var r = MonteCarlo.Run(pnls, startingCash: 1000, simulations: 5000, seed: 99);

        for (var i = 1; i < r.FinalEquityPercentiles.Count; i++)
            r.FinalEquityPercentiles[i].Should().BeGreaterThanOrEqualTo(r.FinalEquityPercentiles[i - 1]);
        for (var i = 1; i < r.MaxDrawdownPercentiles.Count; i++)
            r.MaxDrawdownPercentiles[i].Should().BeGreaterThanOrEqualTo(r.MaxDrawdownPercentiles[i - 1]);
    }

    [Fact]
    public void Run_DeterministicWhenSeeded()
    {
        var pnls = Enumerable.Range(-5, 20).Select(i => (double)i).ToList();
        var a = MonteCarlo.Run(pnls, 1000, simulations: 500, seed: 123);
        var b = MonteCarlo.Run(pnls, 1000, simulations: 500, seed: 123);
        a.MeanFinalEquity.Should().Be(b.MeanFinalEquity);
        a.MeanSharpe.Should().Be(b.MeanSharpe);
    }

    [Fact]
    public void Run_TooFewSimulations_Throws()
    {
        var pnls = new[] { 1.0 };
        var act = () => MonteCarlo.Run(pnls, 100, simulations: 50);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
