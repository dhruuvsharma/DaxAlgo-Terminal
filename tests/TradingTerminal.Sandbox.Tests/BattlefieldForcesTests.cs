using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class BattlefieldForcesTests
{
    [Fact]
    public void FromDepth_PlacesBullsAndBears_OnOppositeSides()
    {
        var depth = new DepthSnapshot(
            DateTime.UtcNow,
            Bids: [new DepthLevel(99.5, 20), new DepthLevel(99.0, 10)],
            Asks: [new DepthLevel(100.5, 15), new DepthLevel(101.0, 25)]);

        var soldiers = BattlefieldForces.FromDepth(depth, tick: 0.5, halfWidthTicks: 10, troopUnit: 5);
        Assert.NotEmpty(soldiers);
        Assert.Contains(soldiers, s => s.IsBid && s.X < 0);
        Assert.Contains(soldiers, s => !s.IsBid && s.X > 0);
        Assert.True(soldiers.Count(s => s.IsBid) >= 2);
        Assert.True(soldiers.Count(s => !s.IsBid) >= 2);
    }

    [Fact]
    public void FromTrades_EmitsStrikesTowardMid()
    {
        var strikes = BattlefieldForces.FromTrades(
            [(101.0, 50, true), (99.0, 40, false)],
            mid: 100.0, tick: 0.5, halfWidthTicks: 10, minSize: 10);
        Assert.Equal(2, strikes.Count);
        Assert.Contains(strikes, s => s.IsBuy);
        Assert.Contains(strikes, s => !s.IsBuy);
    }
}
