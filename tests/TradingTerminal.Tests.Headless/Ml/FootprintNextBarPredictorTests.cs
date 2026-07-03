using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class FootprintNextBarPredictorTests
{
    private const double Tick = 0.25;

    /// <summary>A clean synthetic bar whose POC sits at <paramref name="poc"/>, with a ±2-tick
    /// range, ±1-tick value area / side POCs, constant volume and delta.</summary>
    private static FootprintBarSummary Bar(int index, double poc, long volume = 1_000, long delta = 200) =>
        new(
            StartUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            Poc: poc,
            BuyPoc: poc + Tick,
            SellPoc: poc - Tick,
            High: poc + 2 * Tick,
            Low: poc - 2 * Tick,
            ValueAreaHigh: poc + Tick,
            ValueAreaLow: poc - Tick,
            TotalVolume: volume,
            Delta: delta,
            CumulativeDelta: delta * (index + 1),
            StackedBuy: 1,
            StackedSell: 0,
            QualityMultiplier: 1.0);

    private static FootprintBarSummary DegenerateBar(int index) =>
        new(
            StartUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            Poc: double.NaN, BuyPoc: double.NaN, SellPoc: double.NaN,
            High: double.NaN, Low: double.NaN,
            ValueAreaHigh: double.NaN, ValueAreaLow: double.NaN,
            TotalVolume: 0, Delta: 0, CumulativeDelta: 0,
            StackedBuy: 0, StackedSell: 0, QualityMultiplier: 0.0);

    [Fact]
    public void LearnsAConstantDrift_OneTickPerBar()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        IReadOnlyList<FootprintForecastBar> forecast = Array.Empty<FootprintForecastBar>();
        for (var i = 0; i < 120; i++)
            forecast = ml.OnBarSealed(Bar(i, 100.0 + i * Tick), double.NaN);

        ml.IsReady.Should().BeTrue();
        forecast.Should().NotBeEmpty();

        var lastPoc = 100.0 + 119 * Tick;
        forecast[0].Horizon.Should().Be(1);
        forecast[0].Poc.Should().BeApproximately(lastPoc + Tick, 0.25 * Tick);
        forecast[0].TotalVolume.Should().BeApproximately(1_000, 50);
        forecast[0].Delta.Should().BeGreaterThan(0, "constant positive delta should be learned");

        var accuracy = ml.MlAccuracy;
        accuracy.ScoredCount.Should().BeGreaterThan(50);
        accuracy.DirectionalHitRate.Should().BeGreaterThanOrEqualTo(0.9);
        accuracy.PocMaeTicks.Should().BeLessThan(0.5);
    }

    [Fact]
    public void DirectMultiHorizon_ExtrapolatesTheDrift()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        IReadOnlyList<FootprintForecastBar> forecast = Array.Empty<FootprintForecastBar>();
        for (var i = 0; i < 150; i++)
            forecast = ml.OnBarSealed(Bar(i, 100.0 + i * Tick), double.NaN);

        var lastPoc = 100.0 + 149 * Tick;
        forecast.Should().HaveCount(8, "default MaxHorizon is 8");
        forecast[2].Horizon.Should().Be(3);
        forecast[2].Poc.Should().BeApproximately(lastPoc + 3 * Tick, 0.5 * Tick);
    }

    [Fact]
    public void NotReadyGate_EmitsNoForecastEarly()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        for (var i = 0; i < 10; i++)
        {
            var forecast = ml.OnBarSealed(Bar(i, 100.0 + i * Tick), double.NaN);
            forecast.Should().BeEmpty("fewer than MinSamplesReady updates have happened");
        }
        ml.IsReady.Should().BeFalse();
        ml.SamplesSeen.Should().Be(10);
    }

    [Fact]
    public void IsDeterministic_TwoEnginesSameInputSameOutput()
    {
        var a = new FootprintNextBarPredictor(Tick);
        var b = new FootprintNextBarPredictor(Tick);
        var rng = new Random(11);

        IReadOnlyList<FootprintForecastBar> fa = Array.Empty<FootprintForecastBar>();
        IReadOnlyList<FootprintForecastBar> fb = Array.Empty<FootprintForecastBar>();
        var poc = 100.0;
        for (var i = 0; i < 200; i++)
        {
            poc += (rng.Next(-2, 3)) * Tick;
            var volume = 500 + rng.Next(0, 1_000);
            var delta = rng.Next(-300, 300);
            var bar = Bar(i, poc, volume, delta);
            var baseline = rng.NextDouble() < 0.5 ? poc + Tick : double.NaN;
            fa = a.OnBarSealed(bar, baseline);
            fb = b.OnBarSealed(bar, baseline);
        }

        fa.Should().HaveSameCount(fb);
        for (var i = 0; i < fa.Count; i++)
        {
            fa[i].Poc.Should().Be(fb[i].Poc);
            fa[i].BuyPoc.Should().Be(fb[i].BuyPoc);
            fa[i].SellPoc.Should().Be(fb[i].SellPoc);
            fa[i].TotalVolume.Should().Be(fb[i].TotalVolume);
            fa[i].Delta.Should().Be(fb[i].Delta);
        }
    }

    [Fact]
    public void DegenerateBars_NeverLeakNaNIntoForecasts()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        var rng = new Random(3);
        var poc = 50.0;
        IReadOnlyList<FootprintForecastBar> forecast = Array.Empty<FootprintForecastBar>();
        for (var i = 0; i < 300; i++)
        {
            poc += rng.Next(-1, 2) * Tick;
            forecast = rng.NextDouble() < 0.1
                ? ml.OnBarSealed(DegenerateBar(i), double.NaN)
                : ml.OnBarSealed(Bar(i, poc, 500 + rng.Next(0, 500), rng.Next(-200, 200)), double.NaN);

            foreach (var f in forecast)
            {
                double.IsFinite(f.Poc).Should().BeTrue();
                double.IsFinite(f.BuyPoc).Should().BeTrue();
                double.IsFinite(f.SellPoc).Should().BeTrue();
                double.IsFinite(f.TotalVolume).Should().BeTrue();
                double.IsFinite(f.Delta).Should().BeTrue();
            }
        }
        ml.IsReady.Should().BeTrue("usable bars vastly outnumber degenerate ones");
    }

    [Fact]
    public void BaselineAccuracy_ScoresTheSuppliedConsensus()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        for (var i = 0; i < 60; i++)
        {
            var poc = 100.0 + i * Tick;
            // A perfect baseline: always predicts next POC exactly.
            ml.OnBarSealed(Bar(i, poc), baselineNextPoc: poc + Tick);
        }

        var baseline = ml.BaselineAccuracy;
        baseline.ScoredCount.Should().BeGreaterThan(40);
        baseline.PocMaeTicks.Should().BeApproximately(0.0, 1e-9);
        baseline.DirectionalHitRate.Should().Be(1.0);
    }

    [Fact]
    public void ResetClearsAllState()
    {
        var ml = new FootprintNextBarPredictor(Tick);
        for (var i = 0; i < 60; i++)
            ml.OnBarSealed(Bar(i, 100.0 + i * Tick), double.NaN);
        ml.IsReady.Should().BeTrue();

        ml.Reset();

        ml.IsReady.Should().BeFalse();
        ml.SamplesSeen.Should().Be(0);
        ml.LastForecast.Should().BeEmpty();
        ml.MlAccuracy.ScoredCount.Should().Be(0);
        ml.BaselineAccuracy.ScoredCount.Should().Be(0);
        ml.OnBarSealed(Bar(0, 100.0), double.NaN).Should().BeEmpty();
    }
}
