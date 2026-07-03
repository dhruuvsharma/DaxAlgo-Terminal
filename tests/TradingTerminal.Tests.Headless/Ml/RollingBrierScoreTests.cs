using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class RollingBrierScoreTests
{
    [Fact]
    public void ComputesExactBrierAndBaseRate()
    {
        var s = new RollingBrierScore(window: 10);
        s.Score(0.8, occurred: true);   // (0.8-1)² = 0.04
        s.Score(0.8, occurred: false);  // (0.8-0)² = 0.64
        s.Score(0.0, occurred: false);  // 0

        var snap = s.Snapshot();
        snap.Brier.Should().BeApproximately((0.04 + 0.64 + 0.0) / 3.0, 1e-12);
        snap.BaseRate.Should().BeApproximately(1.0 / 3.0, 1e-12);
        snap.ScoredCount.Should().Be(3);
    }

    [Fact]
    public void WindowWrapsDroppingOldest()
    {
        var s = new RollingBrierScore(window: 2);
        s.Score(1.0, occurred: false);  // err 1 — rolls out
        s.Score(1.0, occurred: true);   // err 0
        s.Score(0.0, occurred: false);  // err 0

        var snap = s.Snapshot();
        snap.Brier.Should().Be(0.0);
        snap.BaseRate.Should().Be(0.5);
        snap.ScoredCount.Should().Be(3, "lifetime count keeps rolling");
    }

    [Fact]
    public void ClampsProbabilitiesAndIgnoresNonFinite()
    {
        var s = new RollingBrierScore();
        s.Score(1.7, occurred: true);        // clamps to 1 → err 0
        s.Score(-0.3, occurred: false);      // clamps to 0 → err 0
        s.Score(double.NaN, occurred: true); // ignored

        var snap = s.Snapshot();
        snap.Brier.Should().Be(0.0);
        snap.ScoredCount.Should().Be(2);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var s = new RollingBrierScore();
        s.Score(0.5, occurred: true);
        s.Reset();

        var snap = s.Snapshot();
        snap.ScoredCount.Should().Be(0);
        snap.Brier.Should().Be(double.NaN);
        snap.BaseRate.Should().Be(double.NaN);
    }
}
