using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class FootprintPriceSnapTests
{
    [Fact]
    public void CryptoDecimals_RoundsBeforeTickSnap()
    {
        // 100.123 with 1 decimal → 100.1, then tick 0.25 → 100.00? 
        // Round(100.1/0.25)=Round(400.4)=400 → 100.0
        var snapped = FootprintPriceSnap.Snap(100.123, 0.25, cryptoDecimals: 1);
        Assert.Equal(100.0, snapped);
    }

    [Fact]
    public void AutoScaleTick_WidensWhenSpanIsLarge()
    {
        var tick = FootprintPriceSnap.AutoScaleTick(priceSpan: 50, instrumentTick: 0.25, targetRows: 20);
        Assert.True(tick >= 2.0); // 50/20 = 2.5 → ~10×0.25
        Assert.Equal(0.0, tick % 0.25, 9);
    }

    [Fact]
    public void MergeRows_AggregatesOntoCoarserTick()
    {
        FootprintFeatureRow[] fine =
        [
            new(100.00, 10, 2, false, false, false, false),
            new(100.25, 5, 1, false, false, false, false),
            new(100.50, 3, 8, false, false, false, false),
        ];
        var merged = FootprintDisplayAggregate.MergeRows(fine, displayTick: 0.5);
        Assert.True(merged.Count <= 2);
        Assert.Equal(18, merged.Sum(r => r.BuyVolume)); // 10 @100 + (5+3) @100.5
        Assert.Equal(11, merged.Sum(r => r.SellVolume));
    }
}
