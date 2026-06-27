using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class TripleBarrierLabelerTests
{
    private sealed record TestBar(double C, double H, double L);

    [Fact]
    public void UpperBarrierTouched_LabelsPositive()
    {
        var bars = new[]
        {
            new TestBar(100, 100, 100), // entry
            new TestBar(100, 100.5, 99.8),
            new TestBar(101, 101.2, 100.5), // upper barrier (100 + 1.0) touched on bar 2
        };
        var r = TripleBarrierLabeler.Apply(bars, b => b.C, b => b.H, b => b.L,
            upperBarrier: 1.0, lowerBarrier: 1.0, timeoutBars: 5);
        r[0].Label.Should().Be(TripleBarrierLabeler.Label.Positive);
        r[0].BarsToOutcome.Should().Be(2);
    }

    [Fact]
    public void LowerBarrierTouched_LabelsNegative()
    {
        var bars = new[]
        {
            new TestBar(100, 100, 100),
            new TestBar(99.5, 100, 99.0),
            new TestBar(98.5, 99.5, 98.0), // hit dn at 99 on bar 1 actually
        };
        var r = TripleBarrierLabeler.Apply(bars, b => b.C, b => b.H, b => b.L,
            upperBarrier: 1.0, lowerBarrier: 1.0, timeoutBars: 5);
        r[0].Label.Should().Be(TripleBarrierLabeler.Label.Negative);
        r[0].BarsToOutcome.Should().Be(1);
    }

    [Fact]
    public void NoTouchWithinTimeout_LabelsNeutral()
    {
        var bars = new[]
        {
            new TestBar(100, 100, 100),
            new TestBar(100.1, 100.2, 99.9),
            new TestBar(100.0, 100.1, 99.95),
        };
        var r = TripleBarrierLabeler.Apply(bars, b => b.C, b => b.H, b => b.L,
            upperBarrier: 1.0, lowerBarrier: 1.0, timeoutBars: 2);
        r[0].Label.Should().Be(TripleBarrierLabeler.Label.Neutral);
    }

    [Fact]
    public void AmbiguousBar_ResolvesToNeutral()
    {
        // One bar touches BOTH barriers — first-touch is ambiguous within bar resolution.
        var bars = new[]
        {
            new TestBar(100, 100, 100),
            new TestBar(100, 101.2, 98.8), // touches both up (101) and dn (99)
        };
        var r = TripleBarrierLabeler.Apply(bars, b => b.C, b => b.H, b => b.L,
            upperBarrier: 1.0, lowerBarrier: 1.0, timeoutBars: 5);
        r[0].Label.Should().Be(TripleBarrierLabeler.Label.Neutral);
    }

    [Fact]
    public void ZeroBarriers_Throws()
    {
        var bars = new[] { new TestBar(100, 100, 100) };
        var act = () => TripleBarrierLabeler.Apply(bars, b => b.C, b => b.H, b => b.L,
            upperBarrier: 0, lowerBarrier: 1, timeoutBars: 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
