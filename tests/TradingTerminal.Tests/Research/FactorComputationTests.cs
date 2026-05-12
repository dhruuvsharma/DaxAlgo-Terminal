using FluentAssertions;
using TradingTerminal.App.Research;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Research;

public sealed class FactorComputationTests
{
    private static IReadOnlyList<Tick> SyntheticTicks(int n, int seed = 7)
    {
        var rng = new Random(seed);
        var ticks = new List<Tick>(n);
        var origin = new DateTime(2026, 5, 12, 14, 0, 0, DateTimeKind.Utc);
        var mid = 100.0;
        for (var i = 0; i < n; i++)
        {
            mid += (rng.NextDouble() - 0.5) * 0.04;
            ticks.Add(new Tick(origin.AddSeconds(i), mid - 0.005, mid + 0.005,
                BidSize: 5 + (long)(rng.NextDouble() * 30),
                AskSize: 5 + (long)(rng.NextDouble() * 30)));
        }
        return ticks;
    }

    [Fact]
    public void ComputeBars_AggregatesEveryNthTick()
    {
        var ticks = SyntheticTicks(1000);
        var bars = FactorComputation.ComputeBars(ticks, barTicks: 100);
        bars.Should().HaveCount(10);
        bars[0].LogReturn.Should().Be(0, "first bar has no previous close");
        bars[1].LogReturn.Should().NotBe(0);
    }

    [Fact]
    public void Correlations_SymmetricAndDiagonalOne()
    {
        var ticks = SyntheticTicks(2000);
        var bars = FactorComputation.ComputeBars(ticks);
        var c = FactorComputation.Correlations(bars);

        for (var i = 0; i < c.FeatureNames.Count; i++)
            c.Values[i, i].Should().BeApproximately(1.0, 1e-9);

        for (var i = 0; i < c.FeatureNames.Count; i++)
            for (var j = 0; j < c.FeatureNames.Count; j++)
                c.Values[i, j].Should().BeApproximately(c.Values[j, i], 1e-9);
    }

    [Fact]
    public void DecileSort_ReturnsTenBucketsWithMonotoneEdges()
    {
        var ticks = SyntheticTicks(3000);
        var bars = FactorComputation.ComputeBars(ticks);
        var r = FactorComputation.DecileSort(bars, "QueueImbalance", forwardBars: 3);
        r.Rows.Should().HaveCount(10);
        for (var i = 1; i < r.Rows.Count; i++)
            r.Rows[i].LowerEdge.Should().BeGreaterThanOrEqualTo(r.Rows[i - 1].LowerEdge);
    }

    [Fact]
    public void DecileSort_TooFewBarsReturnsEmpty()
    {
        var ticks = SyntheticTicks(150);
        var bars = FactorComputation.ComputeBars(ticks, barTicks: 100);
        // Only 1 full bar — decile sort should produce nothing.
        var r = FactorComputation.DecileSort(bars, "LogReturn");
        r.Rows.Should().BeEmpty();
    }
}
