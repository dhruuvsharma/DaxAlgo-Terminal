using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class FootprintFeaturesTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddMinutes(1);

    private static FootprintPrint P(double price, long size, AggressorSide side)
        => new(price, size, side, Start);

    [Fact]
    public void Centroids_ExactVolumeWeightedMeans()
    {
        // tickSize 1. Prices snap to integers.
        // Buys:  100×10, 101×30   ⇒ b̄ = (100*10 + 101*30)/40 = (1000+3030)/40 = 4030/40 = 100.75
        // Sells: 100×20, 99×20    ⇒ s̄ = (100*20 + 99*20)/40 = (2000+1980)/40 = 3980/40 = 99.5
        var prints = new[]
        {
            P(100, 10, AggressorSide.Buy),
            P(101, 30, AggressorSide.Buy),
            P(100, 20, AggressorSide.Sell),
            P(99, 20, AggressorSide.Sell),
        };

        var bar = FootprintFeatures.BuildBar(prints, tickSize: 1.0, Start, End, FeedQuality.RealTape);

        bar.BuyCentroid.Should().BeApproximately(100.75, 1e-9);
        bar.SellCentroid.Should().BeApproximately(99.5, 1e-9);
        bar.BuyVolume.Should().Be(40);
        bar.SellVolume.Should().Be(40);

        // Volume centroid: rows total — 101:30, 100:30, 99:20 ⇒ Σ price·vol = 3030+3000+1980=8010, /80
        bar.VolumeCentroid.Should().BeApproximately(8010.0 / 80.0, 1e-9);
    }

    [Fact]
    public void Poc_IsArgmaxTotalVolumeRow()
    {
        // 100 has the most total volume (50+10=60) vs 101 (40) vs 99 (5).
        var prints = new[]
        {
            P(101, 40, AggressorSide.Buy),
            P(100, 50, AggressorSide.Buy),
            P(100, 10, AggressorSide.Sell),
            P(99, 5, AggressorSide.Sell),
        };

        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape);

        bar.PocPrice.Should().Be(100.0);
    }

    [Fact]
    public void PerBarDelta_AndCumulativeDelta_ChainViaCumDeltaBefore()
    {
        // Bar buy 70, sell 30 ⇒ delta +40. With cumBefore = 15 ⇒ cumulative 55.
        var prints = new[]
        {
            P(100, 70, AggressorSide.Buy),
            P(100, 30, AggressorSide.Sell),
        };

        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape, cumulativeDeltaBefore: 15);

        bar.Delta.Should().Be(40);
        bar.CumulativeDelta.Should().Be(55);
    }

    [Fact]
    public void RowImbalances_3To1_FlagsAndStackedCounts()
    {
        // tickSize 1, ratio 3:1 (default). Rows high→low: 102, 101, 100.
        //   102: buy 40, sell 0
        //   101: buy 30, sell 1
        //   100: buy 0,  sell 40
        // askImb(row) = buy >= 3 * sellBelow:
        //   102: buy40 vs sellBelow(101)=1 → 40>=3 ✓ askImb
        //   101: buy30 vs sellBelow(100)=40 → 30>=120 ✗
        //   100: buy0 → ✗
        // bidImb(row) = sell >= 3 * buyAbove:
        //   102: sell0 → ✗
        //   101: sell1 vs buyAbove(102)=40 → 1>=120 ✗
        //   100: sell40 vs buyAbove(101)=30 → 40>=90 ✗
        var prints = new[]
        {
            P(102, 40, AggressorSide.Buy),
            P(101, 30, AggressorSide.Buy),
            P(101, 1, AggressorSide.Sell),
            P(100, 40, AggressorSide.Sell),
        };

        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape);
        var rows = bar.Rows; // high → low

        rows[0].Price.Should().Be(102);
        rows[0].AskImbalance.Should().BeTrue();
        rows[0].BidImbalance.Should().BeFalse();
        rows[1].AskImbalance.Should().BeFalse();
        rows[2].BidImbalance.Should().BeFalse();

        bar.StackedBuy.Should().Be(1);  // one ask-imbalanced row
        bar.StackedSell.Should().Be(0);
    }

    [Fact]
    public void StackedBuy_CountsConsecutiveAskImbalancedRows()
    {
        // Three rows each with strong buy vs the sell one below ⇒ stacked run.
        // Rows high→low: 103,102,101,100.
        //   103: buy 100, sell 0
        //   102: buy 100, sell 0
        //   101: buy 100, sell 0
        //   100: buy 0,   sell 1   (provides the small sellBelow for 101)
        // askImb: 103 (sellBelow102=0 → buy>=0 with buy>0 ✓), 102 (sellBelow101=0 ✓),
        //         101 (sellBelow100=1 → 100>=3 ✓), 100 (buy0 ✗) ⇒ run of 3.
        var prints = new[]
        {
            P(103, 100, AggressorSide.Buy),
            P(102, 100, AggressorSide.Buy),
            P(101, 100, AggressorSide.Buy),
            P(100, 1, AggressorSide.Sell),
        };

        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape);

        bar.StackedBuy.Should().Be(3);
    }

    [Fact]
    public void ZeroPrintDetection_FlagsRowsWithNoVolumeOnOneSide()
    {
        var prints = new[]
        {
            P(100, 10, AggressorSide.Buy),   // 100: buy only ⇒ ZeroBid
            P(99, 10, AggressorSide.Sell),   // 99: sell only ⇒ ZeroAsk
        };

        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape);
        var top = bar.Rows[0]; // 100
        var bottom = bar.Rows[1]; // 99

        top.Price.Should().Be(100);
        top.ZeroBid.Should().BeTrue();  // no sell volume
        top.ZeroAsk.Should().BeFalse();

        bottom.Price.Should().Be(99);
        bottom.ZeroAsk.Should().BeTrue(); // no buy volume
        bottom.ZeroBid.Should().BeFalse();
    }

    [Fact]
    public void UnknownAggressor_SplitsToPreserveTotalVolume()
    {
        // Size 11 unknown ⇒ buy 6, sell 5 (half rounds up to buy).
        var prints = new[] { P(100, 11, AggressorSide.Unknown) };
        var bar = FootprintFeatures.BuildBar(prints, 1.0, Start, End, FeedQuality.RealTape);

        bar.TotalVolume.Should().Be(11);
        bar.BuyVolume.Should().Be(6);
        bar.SellVolume.Should().Be(5);
    }

    [Fact]
    public void EmptyPrints_ProduceEmptyBar()
    {
        var bar = FootprintFeatures.BuildBar(Array.Empty<FootprintPrint>(), 1.0, Start, End, FeedQuality.None);
        bar.Rows.Should().BeEmpty();
        bar.TotalVolume.Should().Be(0);
        bar.Delta.Should().Be(0);
    }

    [Fact]
    public void BuildBar_NonPositiveTickSize_Throws()
    {
        var act = () => FootprintFeatures.BuildBar(Array.Empty<FootprintPrint>(), 0.0, Start, End, FeedQuality.None);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── SuggestRowSize ───────────────────────────────────────────────────────────────

    [Fact]
    public void SuggestRowSize_Atr20Ticks_Target20Rows_IsOneTick()
    {
        // ATR = 20 ticks = 20 * 0.25 = 5.0 price; target 20 rows ⇒ raw = 5.0/20 = 0.25 = 1 tick.
        const double tick = 0.25;
        var size = FootprintFeatures.SuggestRowSize(barAtr: 20 * tick, instrumentTickSize: tick, targetRows: 20);
        size.Should().BeApproximately(tick, 1e-12);
    }

    [Fact]
    public void SuggestRowSize_ClampsToAtLeastOneInstrumentTick()
    {
        // Tiny ATR ⇒ raw rounds to < 1 tick ⇒ clamp to one tick.
        const double tick = 0.25;
        var size = FootprintFeatures.SuggestRowSize(barAtr: 0.1, instrumentTickSize: tick, targetRows: 20);
        size.Should().BeApproximately(tick, 1e-12);
    }

    [Fact]
    public void SuggestRowSize_NonPositiveAtr_FallsBackToTick()
    {
        FootprintFeatures.SuggestRowSize(0, 0.5).Should().Be(0.5);
        FootprintFeatures.SuggestRowSize(double.NaN, 0.5).Should().Be(0.5);
    }

    [Fact]
    public void SuggestRowSize_SnapsToMultipleOfTick()
    {
        // ATR 100 price, 20 rows ⇒ raw 5.0; tick 2 ⇒ round(5/2)=3 ⇒ 6.
        FootprintFeatures.SuggestRowSize(barAtr: 100, instrumentTickSize: 2.0, targetRows: 20)
            .Should().BeApproximately(6.0, 1e-12);
    }

    // ── SyntheticPrints ───────────────────────────────────────────────────────────────

    [Fact]
    public void SyntheticPrints_ClassificationMatchesMicrostructure()
    {
        // (tradePrice, size, bid, ask, time)
        var updates = new[]
        {
            (TradePrice: 100.5, Size: 10L, Bid: 100.0, Ask: 101.0, TimeUtc: Start),               // mid, no prior ⇒ Unknown
            (TradePrice: 101.0, Size: 5L, Bid: 100.0, Ask: 101.0, TimeUtc: Start.AddSeconds(1)),  // at ask ⇒ Buy
            (TradePrice: 100.0, Size: 8L, Bid: 100.0, Ask: 101.0, TimeUtc: Start.AddSeconds(2)),  // at bid ⇒ Sell
            (TradePrice: 100.5, Size: 3L, Bid: 100.0, Ask: 101.0, TimeUtc: Start.AddSeconds(3)),  // mid, prior 100.0 ⇒ up tick ⇒ Buy
        };

        var prints = FootprintFeatures.SyntheticPrints(updates).ToList();
        prints.Should().HaveCount(4);

        // Reproduce the classifier's stateful walk to assert agreement exactly.
        double priorTrade = 0;
        var priorClass = AggressorSide.Unknown;
        for (var i = 0; i < updates.Length; i++)
        {
            var u = updates[i];
            var expected = Microstructure.ClassifyAggressor(u.TradePrice, u.Bid, u.Ask, priorTrade, priorClass);
            prints[i].Aggressor.Should().Be(expected);
            prints[i].Price.Should().Be(u.TradePrice);
            prints[i].Size.Should().Be(u.Size);
            priorTrade = u.TradePrice;
            if (expected != AggressorSide.Unknown) priorClass = expected;
        }

        // Sanity on the explicit cases.
        prints[0].Aggressor.Should().Be(AggressorSide.Unknown);
        prints[1].Aggressor.Should().Be(AggressorSide.Buy);
        prints[2].Aggressor.Should().Be(AggressorSide.Sell);
        prints[3].Aggressor.Should().Be(AggressorSide.Buy);
    }

    [Fact]
    public void SyntheticPrints_SkipsNonPositiveSizeOrPrice()
    {
        var updates = new[]
        {
            (TradePrice: 100.0, Size: 0L, Bid: 99.0, Ask: 101.0, TimeUtc: Start),
            (TradePrice: 0.0, Size: 5L, Bid: 99.0, Ask: 101.0, TimeUtc: Start),
            (TradePrice: 100.0, Size: 5L, Bid: 99.0, Ask: 101.0, TimeUtc: Start),
        };
        FootprintFeatures.SyntheticPrints(updates).Should().HaveCount(1);
    }

    // ── FeedQuality multipliers ───────────────────────────────────────────────────────

    [Fact]
    public void FeedQuality_Multipliers()
    {
        FeedQuality.RealTape.Multiplier().Should().Be(1.0);
        FeedQuality.SyntheticL1.Multiplier().Should().Be(0.4);
        FeedQuality.None.Multiplier().Should().Be(0.0);
    }
}
