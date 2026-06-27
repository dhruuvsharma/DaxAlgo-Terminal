using FluentAssertions;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class IndicatorsTests
{
    [Fact]
    public void Sma_AveragesLastNValues()
    {
        var sma = new Indicators.SimpleMovingAverage(3);
        sma.Push(1); sma.Push(2);
        sma.IsReady.Should().BeFalse();
        sma.Push(3);
        sma.IsReady.Should().BeTrue();
        sma.Value.Should().BeApproximately(2.0, 1e-12);
        sma.Push(4);
        sma.Value.Should().BeApproximately(3.0, 1e-12);
    }

    [Fact]
    public void RollingStdev_MatchesBesselCorrection()
    {
        // For [1, 2, 3, 4]: sample stdev = sqrt(5/3) ≈ 1.29099
        var s = new Indicators.RollingStdev(4);
        s.Push(1); s.Push(2); s.Push(3); s.Push(4);
        s.IsReady.Should().BeTrue();
        s.Value.Should().BeApproximately(Math.Sqrt(5.0 / 3.0), 1e-9);
    }

    [Fact]
    public void Ema_RecursiveFormHoldsExactly()
    {
        // α = 2/(3+1) = 0.5. Push 10 then 20 → 0.5·20 + 0.5·10 = 15.
        var ema = new Indicators.ExponentialMovingAverage(3);
        ema.Push(10);
        ema.Value.Should().Be(10);
        ema.Push(20);
        ema.Value.Should().BeApproximately(15.0, 1e-12);
    }

    [Fact]
    public void Rsi_AllUpTicks_Saturates100()
    {
        var rsi = new Indicators.RelativeStrengthIndex(14);
        for (var i = 0; i < 50; i++) rsi.Push(100 + i);
        rsi.IsReady.Should().BeTrue();
        rsi.Value.Should().Be(100);
    }

    [Fact]
    public void Rsi_AllDownTicks_SaturatesNearZero()
    {
        var rsi = new Indicators.RelativeStrengthIndex(14);
        for (var i = 0; i < 50; i++) rsi.Push(100 - i);
        rsi.IsReady.Should().BeTrue();
        rsi.Value.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Atr_AveragesAbsTickDeltas()
    {
        // Deltas |2-1|, |4-2|, |3-4|, |6-3| = 1, 2, 1, 3 → mean = 1.75
        var atr = new Indicators.AverageTrueRange(4);
        atr.Push(1); atr.Push(2); atr.Push(4); atr.Push(3); atr.Push(6);
        atr.IsReady.Should().BeTrue();
        atr.Value.Should().BeApproximately(1.75, 1e-12);
    }
}
