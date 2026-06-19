using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>
/// Covers the Hawkes self-exciting intensity tracker: it returns the baseline with no events,
/// decays after a single event, spikes during a burst, and the recursive instance matches the
/// pure summation helper exactly.
/// </summary>
public sealed class HawkesProcessTests
{
    [Fact]
    public void NoEvents_ReturnsBaseline()
    {
        var h = new HawkesProcess(baselineMu: 0.5, alpha: 0.3, beta: 0.5);
        h.Intensity(10.0).Should().Be(0.5);
    }

    [Fact]
    public void SingleEvent_DecaysTowardBaseline()
    {
        var h = new HawkesProcess(baselineMu: 0.0, alpha: 0.4, beta: 0.5);
        h.Add(0.0);

        var atEvent = h.Intensity(0.0);     // μ + α·1 = 0.4
        var later = h.Intensity(10.0);      // decayed

        atEvent.Should().BeApproximately(0.4, 1e-12);
        later.Should().BeLessThan(atEvent);
        later.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Burst_RaisesIntensity_AboveSingleEvent()
    {
        var burst = new HawkesProcess(0.0, 0.4, 0.5);
        for (var i = 0; i < 20; i++) burst.Add(i * 0.1);   // 20 trades over ~2s

        var single = new HawkesProcess(0.0, 0.4, 0.5);
        single.Add(1.9);

        burst.Intensity(2.0).Should().BeGreaterThan(single.Intensity(2.0));
    }

    [Fact]
    public void RecursiveInstance_MatchesPureHelper()
    {
        const double mu = 0.2, alpha = 0.3, beta = 0.7;
        var times = new[] { 0.0, 0.3, 0.35, 1.2, 2.0, 2.05, 5.0 };
        const double now = 6.0;

        var h = new HawkesProcess(mu, alpha, beta);
        foreach (var t in times) h.Add(t);

        h.Intensity(now).Should().BeApproximately(
            HawkesProcess.IntensityAt(times, now, mu, alpha, beta), 1e-9);
    }
}
