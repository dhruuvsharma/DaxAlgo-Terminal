using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

public sealed class ModernIchimokuTests
{
    [Fact]
    public void Update_EmitsDisplacedSpans_AfterWarmup()
    {
        var eng = new ModernIchimoku();
        ModernIchimoku.Snapshot last = default;
        var spanReady = false;
        for (var i = 0; i < 120; i++)
        {
            var px = 100 + i * 0.2;
            last = eng.Update(px + 0.5, px - 0.5, px - 0.1, px);
            if (double.IsFinite(last.SpanA) && double.IsFinite(last.SpanB))
                spanReady = true;
        }

        Assert.True(spanReady);
        Assert.True(double.IsFinite(last.Tenkan));
        Assert.True(double.IsFinite(last.Kijun));
        Assert.True(last.Atr > 0);
    }

    [Fact]
    public void FromBars_Chart_AlignsSeriesAndAllowsMarkers()
    {
        var bars = new List<OhlcvBar>();
        var px = 100.0;
        for (var i = 0; i < 160; i++)
        {
            px += i % 17 < 10 ? 0.35 : -0.25;
            bars.Add(new OhlcvBar(
                new InstrumentId(1), BarSize.OneMinute,
                DateTime.UnixEpoch.AddMinutes(i),
                px - 0.1, px + 0.3, px - 0.3, px, 100,
                BrokerKind.Simulated, true));
        }

        var series = ModernIchimokuChart.FromBars(bars);
        Assert.Equal(bars.Count, series.Tenkan.Count);
        Assert.Equal(bars.Count, series.SpanA.Count);
        Assert.Equal(bars.Count, series.Chikou.Count);
        Assert.Contains(series.Tenkan, v => double.IsFinite(v));
        Assert.Contains(series.SpanA, v => double.IsFinite(v));
        // Chikou lags: last displacement slots are NaN
        Assert.True(double.IsNaN(series.Chikou[^1]));
        Assert.True(double.IsFinite(series.Chikou[0]));
    }

    [Fact]
    public void TkOrKumo_EmitsRawAndQualified_OnReversalTape()
    {
        var eng = new ModernIchimoku(minCloudDistanceAtr: 0);
        var sawRaw = false;
        var sawQualified = false;
        var px = 50.0;
        for (var i = 0; i < 260; i++)
        {
            // Long grind up, then collapse hard so lagged close is still high → bear chikouOk.
            var up = i < 140;
            px += up ? 0.5 : -2.2;
            var open = up ? px - 0.15 : px + 0.15;
            var close = px;
            var s = eng.Update(Math.Max(open, close) + 0.25, Math.Min(open, close) - 0.25, open, close);
            if (s.RawSignal != ModernIchimoku.SignalKind.None) sawRaw = true;
            if (s.Qualified) sawQualified = true;
        }

        Assert.True(sawRaw);
        Assert.True(sawQualified);
    }
}
