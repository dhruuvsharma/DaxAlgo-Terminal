using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class LuxSignalMarkersTests
{
    [Fact]
    public void FromBars_EmitsTrail_AfterWarmup()
    {
        var bars = new List<OhlcvBar>();
        var px = 100.0;
        for (var i = 0; i < 80; i++)
        {
            px += i < 50 ? 0.4 : -0.15;
            bars.Add(new OhlcvBar(
                new InstrumentId(1), BarSize.OneMinute,
                DateTime.UnixEpoch.AddMinutes(i),
                px - 0.1, px + 0.2, px - 0.2, px, 100,
                BrokerKind.Simulated, true));
        }

        var (markers, trail) = LuxSignalMarkers.FromBars(bars);
        Assert.Equal(bars.Count, trail.Count);
        Assert.Contains(trail, v => double.IsFinite(v) && v > 0);
        Assert.All(markers, m => Assert.True(m.Index >= 0 && m.Index < bars.Count));
    }

    [Fact]
    public void FromBars_Empty_ReturnsEmpty()
    {
        var (markers, trail) = LuxSignalMarkers.FromBars([]);
        Assert.Empty(markers);
        Assert.Empty(trail);
    }
}
