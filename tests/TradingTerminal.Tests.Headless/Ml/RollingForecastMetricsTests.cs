using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class RollingForecastMetricsTests
{
    [Fact]
    public void ComputesExactMaeAndHitRate()
    {
        var m = new RollingForecastMetrics(window: 10);
        m.Score(1.0, 2.0);   // err 1, hit (both positive)
        m.Score(-2.0, 1.0);  // err 3, miss (sign disagrees)
        m.Score(0.5, 0.5);   // err 0, hit

        var snap = m.Snapshot();
        snap.PocMaeTicks.Should().BeApproximately((1.0 + 3.0 + 0.0) / 3.0, 1e-12);
        snap.DirectionalHitRate.Should().BeApproximately(2.0 / 3.0, 1e-12);
        snap.ScoredCount.Should().Be(3);
    }

    [Fact]
    public void ZeroRealizedMove_HitsOnlyWhenPredictionIsFlat()
    {
        var m = new RollingForecastMetrics(window: 10);
        m.Score(0.4, 0.0);   // |pred| < 0.5 tick → hit
        m.Score(0.6, 0.0);   // called a move that didn't happen → miss
        m.Score(-0.6, 0.0);  // miss

        m.Snapshot().DirectionalHitRate.Should().BeApproximately(1.0 / 3.0, 1e-12);
    }

    [Fact]
    public void WindowRollsOver_DroppingOldestScores()
    {
        var m = new RollingForecastMetrics(window: 2);
        m.Score(10.0, 0.0);  // err 10, miss — should roll out
        m.Score(1.0, 1.0);   // err 0, hit
        m.Score(2.0, 2.0);   // err 0, hit

        var snap = m.Snapshot();
        snap.PocMaeTicks.Should().Be(0.0);
        snap.DirectionalHitRate.Should().Be(1.0);
        snap.ScoredCount.Should().Be(3, "lifetime count keeps rolling past the window");
    }

    [Fact]
    public void NonFiniteInputsAreIgnored()
    {
        var m = new RollingForecastMetrics();
        m.Score(double.NaN, 1.0);
        m.Score(1.0, double.PositiveInfinity);

        m.Snapshot().ScoredCount.Should().Be(0);
        m.Snapshot().PocMaeTicks.Should().Be(double.NaN);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var m = new RollingForecastMetrics();
        m.Score(1.0, 1.0);
        m.Reset();

        var snap = m.Snapshot();
        snap.ScoredCount.Should().Be(0);
        snap.PocMaeTicks.Should().Be(double.NaN);
        snap.DirectionalHitRate.Should().Be(double.NaN);
    }
}
