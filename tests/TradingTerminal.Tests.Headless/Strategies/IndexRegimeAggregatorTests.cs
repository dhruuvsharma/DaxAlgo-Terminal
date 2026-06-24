using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.IndexRegime;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public class IndexRegimeAggregatorTests
{
    private static readonly DateTime Now = new(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);

    private static IndexComponent Comp(string sym, double weight) =>
        new(sym, sym, weight, Contract.UsStock(sym));

    /// <summary>Snapshot whose every timeframe column carries the trend score returned by
    /// <paramref name="trendByLabel"/> (-8..+8). Cells are irrelevant to the aggregation.</summary>
    private static AdvancedRegimeSnapshot Snap(string sym, Func<string, int> trendByLabel, bool unavailable = false)
    {
        var cols = AdvancedTimeframe.Defaults
            .Select(tf => new AdvancedRegimeColumn(tf, Array.Empty<AdvancedRegimeCell>(), trendByLabel(tf.Label), 0))
            .ToList();
        return new AdvancedRegimeSnapshot(sym, cols, Now, unavailable);
    }

    private static List<(IndexComponent, AdvancedRegimeSnapshot)> Inputs(
        params (IndexComponent, AdvancedRegimeSnapshot)[] items) => items.ToList();

    [Fact]
    public void AllColumnsStronglyUp_GivesCompositePlusOne_StrongUp()
    {
        var inputs = Inputs((Comp("AAPL", 1.0), Snap("AAPL", _ => 8)));

        var snap = IndexRegimeAggregator.Aggregate("X", RegimeHorizon.Intraday, inputs, Now);

        snap.CompositeScore.Should().BeApproximately(1.0, 1e-9);
        snap.Band.Should().Be(CellSignal.StrongUp);
        snap.Constituents[0].StockScore.Should().BeApproximately(1.0, 1e-9);
        snap.Constituents[0].HasData.Should().BeTrue();
        snap.ConstituentsWithData.Should().Be(1);
    }

    [Fact]
    public void Horizon_ReweightsTimeframes_FlippingTheStockScoreSign()
    {
        // Fast columns bullish, slow columns bearish.
        int trend(string label) => label is "1m" or "3m" or "5m" ? 8 : -8;
        var inputs = Inputs((Comp("AAPL", 1.0), Snap("AAPL", trend)));

        var scalp = IndexRegimeAggregator.Aggregate("X", RegimeHorizon.Scalp, inputs, Now).Constituents[0].StockScore;
        var position = IndexRegimeAggregator.Aggregate("X", RegimeHorizon.Position, inputs, Now).Constituents[0].StockScore;

        scalp.Should().BeGreaterThan(0, "the scalp horizon leans on the bullish fast columns");
        position.Should().BeLessThan(0, "the position horizon leans on the bearish slow columns");
    }

    [Fact]
    public void UnavailableConstituent_DropsOut_AndItsWeightIsRedistributed()
    {
        var inputs = Inputs(
            (Comp("AAA", 0.7), Snap("AAA", _ => 8)),
            (Comp("BBB", 0.3), Snap("BBB", _ => -8, unavailable: true)));

        var snap = IndexRegimeAggregator.Aggregate("X", RegimeHorizon.Intraday, inputs, Now);

        // BBB has no data, so AAA carries the whole (renormalised) weight → composite ≈ AAA's +1.
        snap.CompositeScore.Should().BeApproximately(1.0, 1e-9);
        var bbb = snap.Constituents.Single(c => c.Symbol == "BBB");
        bbb.HasData.Should().BeFalse();
        bbb.NormalizedWeight.Should().Be(0);
        bbb.Contribution.Should().Be(0);
        snap.Constituents.Single(c => c.Symbol == "AAA").NormalizedWeight.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Composite_IsIndexWeightedSumOfStockScores()
    {
        var inputs = Inputs(
            (Comp("AAA", 0.8), Snap("AAA", _ => 8)),    // +1
            (Comp("BBB", 0.2), Snap("BBB", _ => -8)));   // -1

        var snap = IndexRegimeAggregator.Aggregate("X", RegimeHorizon.Intraday, inputs, Now);

        snap.CompositeScore.Should().BeApproximately(0.8 * 1.0 + 0.2 * -1.0, 1e-9);
        snap.Band.Should().Be(CellSignal.StrongUp);
        snap.BullishCount.Should().Be(1);
        snap.BearishCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0.50, CellSignal.StrongUp)]
    [InlineData(0.49, CellSignal.Up)]
    [InlineData(0.15, CellSignal.Up)]
    [InlineData(0.14, CellSignal.Neutral)]
    [InlineData(0.0, CellSignal.Neutral)]
    [InlineData(-0.14, CellSignal.Neutral)]
    [InlineData(-0.15, CellSignal.Down)]
    [InlineData(-0.49, CellSignal.Down)]
    [InlineData(-0.50, CellSignal.StrongDown)]
    public void BandFor_MapsScoreToFiveLevelBand(double score, CellSignal expected) =>
        IndexRegimeAggregator.BandFor(score).Should().Be(expected);
}
