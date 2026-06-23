using FluentAssertions;
using TradingTerminal.Core.Regime;
using Xunit;

namespace TradingTerminal.Tests.Regime;

public sealed class MarketRegimeCalculatorTests
{
    // A steadily rising series sits above all its moving averages and has positive ROC.
    private static double[] Rising(int n, double start = 100, double step = 0.5)
    {
        var a = new double[n];
        for (var i = 0; i < n; i++) a[i] = start + step * i;
        return a;
    }

    private static double[] Falling(int n, double start = 200, double step = 0.5)
    {
        var a = new double[n];
        for (var i = 0; i < n; i++) a[i] = start - step * i;
        return a;
    }

    [Fact]
    public void Weights_sum_to_one()
    {
        var sum = Enum.GetValues<RegimeCategory>().Sum(MarketRegimeCalculator.Weight);
        sum.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Empty_inputs_score_neutral_50()
    {
        var snap = MarketRegimeCalculator.Compute(new RegimeInputs(), previousScore: null, DateTime.UtcNow);

        // Every category degrades to its neutral 50 → composite 50.
        snap.CompositeScore.Should().BeApproximately(50, 0.5);
        snap.State.Should().Be(RegimeState.Neutral);
        snap.Categories.Should().HaveCount(10);
    }

    [Fact]
    public void Risk_on_inputs_score_high_and_band_greedy()
    {
        var sectors = new[] { "XLK", "XLF", "XLE", "XLV" }
            .ToDictionary(s => s, _ => Rising(250));

        var inputs = new RegimeInputs
        {
            Vix = 12,                 // low vol → greedy
            Vix3m = 16,               // contango
            Skew = 110,
            SpxCloses = Rising(250),
            SpyCloses = Rising(250),
            RspCloses = Rising(250, step: 0.6), // equal-weight leading → broad
            SectorCloses = sectors,
            CnnFearGreed = 80,
            AaiiBull = 50,
            AaiiBear = 20,
        };

        var snap = MarketRegimeCalculator.Compute(inputs, previousScore: null, DateTime.UtcNow);

        snap.CompositeScore.Should().BeGreaterThan(60);
        snap.State.Should().BeOneOf(RegimeState.Greed, RegimeState.ExtremeGreed);
    }

    [Fact]
    public void Risk_off_inputs_score_low_and_band_fearful()
    {
        var sectors = new[] { "XLK", "XLF", "XLE", "XLV" }
            .ToDictionary(s => s, _ => Falling(250));

        var inputs = new RegimeInputs
        {
            Vix = 40,                 // panic
            Vix3m = 30,               // backwardation
            Skew = 150,
            SpxCloses = Falling(250),
            SpyCloses = Falling(250),
            RspCloses = Falling(250, step: 0.7),
            GldCloses = Rising(60),   // gold up vs equities → risk-off
            TltCloses = Rising(60),   // bonds up vs equities → risk-off
            SectorCloses = sectors,
            CnnFearGreed = 12,
            AaiiBull = 20,
            AaiiBear = 55,
        };

        var snap = MarketRegimeCalculator.Compute(inputs, previousScore: null, DateTime.UtcNow);

        snap.CompositeScore.Should().BeLessThan(40);
        snap.State.IsRiskOff().Should().BeTrue();
    }

    [Fact]
    public void Missing_fred_degrades_credit_liquidity_macro()
    {
        var inputs = new RegimeInputs { Vix = 18, SpxCloses = Rising(250) };
        var snap = MarketRegimeCalculator.Compute(inputs, previousScore: null, DateTime.UtcNow);

        var degraded = snap.Categories.Where(c => c.Degraded).Select(c => c.Category).ToHashSet();
        degraded.Should().Contain(new[]
        {
            RegimeCategory.Credit, RegimeCategory.Liquidity, RegimeCategory.Macro,
        });
    }

    [Fact]
    public void Contribution_equals_score_times_weight()
    {
        var snap = MarketRegimeCalculator.Compute(new RegimeInputs(), previousScore: null, DateTime.UtcNow);
        foreach (var c in snap.Categories)
            c.Contribution.Should().BeApproximately(Math.Round(c.Score * c.Weight * 10) / 10, 1e-9);
    }
}
