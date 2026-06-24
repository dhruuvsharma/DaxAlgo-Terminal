using FluentAssertions;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Unit tests for the OBI(T) primitives from arXiv:2507.22712 (trade-based imbalance + the
/// nine-bin symmetric regime grid) consumed by the Filtered Order-Flow Imbalance strategy.
/// </summary>
public sealed class OrderFlowImbalanceTests
{
    [Fact]
    public void TradeImbalance_IsZeroWhenNoTrades()
    {
        OrderFlowImbalance.TradeImbalance(0, 0).Should().Be(0);
    }

    [Theory]
    [InlineData(10, 0, 1.0)]    // all buys → +1
    [InlineData(0, 10, -1.0)]   // all sells → −1
    [InlineData(7, 3, 0.4)]     // (7−3)/10
    [InlineData(3, 7, -0.4)]
    [InlineData(5, 5, 0.0)]     // balanced → neutral
    public void TradeImbalance_MatchesEq17(long buy, long sell, double expected)
    {
        OrderFlowImbalance.TradeImbalance(buy, sell).Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void Regime_IsNeutralAroundZero()
    {
        OrderFlowImbalance.Regime(0.0).Should().Be(0);
        OrderFlowImbalance.Regime(0.05).Should().Be(0);
        OrderFlowImbalance.Regime(-0.05).Should().Be(0);
    }

    [Fact]
    public void Regime_IsExtremeAtBounds()
    {
        OrderFlowImbalance.Regime(1.0).Should().Be(4);
        OrderFlowImbalance.Regime(-1.0).Should().Be(-4);
    }

    [Fact]
    public void Regime_IsAntisymmetric()
    {
        for (var x = -1.0; x <= 1.0; x += 0.1)
            OrderFlowImbalance.Regime(x).Should().Be(-OrderFlowImbalance.Regime(-x));
    }

    [Fact]
    public void Regime_IsMonotonicNonDecreasing()
    {
        var prev = OrderFlowImbalance.Regime(-1.0);
        for (var x = -1.0; x <= 1.0; x += 0.01)
        {
            var r = OrderFlowImbalance.Regime(x);
            r.Should().BeGreaterThanOrEqualTo(prev);
            prev = r;
        }
    }

    [Fact]
    public void Regime_ClampsOutOfRangeInput()
    {
        OrderFlowImbalance.Regime(5.0).Should().Be(4);
        OrderFlowImbalance.Regime(-5.0).Should().Be(-4);
        OrderFlowImbalance.Regime(double.NaN).Should().Be(0);
    }

    [Theory]
    [InlineData(0.9, 3, true)]    // regime +4 ≥ 3
    [InlineData(0.6, 3, true)]    // regime +3 ≥ 3
    [InlineData(0.3, 3, false)]   // regime +1 < 3
    [InlineData(-0.9, 3, true)]
    [InlineData(0.0, 1, false)]   // neutral is never "strong"
    public void IsStrong_GatesOnRegimeMagnitude(double obi, int strong, bool expected)
    {
        OrderFlowImbalance.IsStrong(obi, strong).Should().Be(expected);
    }
}
