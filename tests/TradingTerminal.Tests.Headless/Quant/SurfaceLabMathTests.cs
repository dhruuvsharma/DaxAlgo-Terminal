using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Quant.Surfaces;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>Statistics registry math for the 3D Surface Lab (Core/Quant/Surfaces).</summary>
public sealed class SurfaceMetricsTests
{
    private static SurfaceCellSample Sample(double[] returns, double ppy = 252) =>
        new(returns, null, ppy);

    [Fact]
    public void MeanMedianStd_KnownSeries()
    {
        var s = Sample(new[] { 0.01, 0.02, 0.03, 0.10 });
        SurfaceMetricRegistry.Mean(s).Should().BeApproximately(0.04, 1e-12);
        SurfaceMetricRegistry.Median(s).Should().BeApproximately(0.025, 1e-12);
        SurfaceMetricRegistry.StdDev(s).Should().BeGreaterThan(0);
    }

    [Fact]
    public void RealizedVol_Annualizes()
    {
        var s = Sample(new[] { 0.0, 0.02, 0.0, 0.02 });
        var std = Math.Sqrt(4.0 / 3.0 * 0.0001);
        SurfaceMetricRegistry.RealizedVol(s).Should().BeApproximately(std * Math.Sqrt(252), 1e-9);
    }

    [Fact]
    public void ValueAtRisk_IsPositiveLossQuantile()
    {
        var returns = Enumerable.Range(1, 100).Select(i => -i / 1000.0).ToArray(); // -0.1% … -10%
        var s = Sample(returns);
        SurfaceMetricRegistry.ValueAtRisk(s, 0.95).Should().BeGreaterThan(0.09).And.BeLessThan(0.10);
        SurfaceMetricRegistry.ConditionalVaR(s, 0.95).Should()
            .BeGreaterThan(SurfaceMetricRegistry.ValueAtRisk(s, 0.95));
    }

    [Fact]
    public void NormalCdf_MatchesKnownValues()
    {
        SurfaceMetricRegistry.NormalCdf(0).Should().BeApproximately(0.5, 1e-6);
        SurfaceMetricRegistry.NormalCdf(1.96).Should().BeApproximately(0.975, 1e-3);
        SurfaceMetricRegistry.NormalCdf(-1.96).Should().BeApproximately(0.025, 1e-3);
    }

    [Fact]
    public void Skewness_SymmetricIsZero()
    {
        SurfaceMetricRegistry.Skewness(Sample(new[] { -0.02, -0.01, 0.0, 0.01, 0.02 }))
            .Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void Autocorr1_AlternatingSeriesIsNegative_ConstantTrendPositive()
    {
        SurfaceMetricRegistry.Autocorr1(Sample(new[] { 0.01, -0.01, 0.01, -0.01, 0.01, -0.01 }))
            .Should().BeLessThan(-0.5);
        SurfaceMetricRegistry.Autocorr1(Sample(new[] { -0.03, -0.02, -0.01, 0.01, 0.02, 0.03 }))
            .Should().BeGreaterThan(0.3);
    }

    [Fact]
    public void Amihud_UsesDollarVolumes()
    {
        var s = new SurfaceCellSample(new[] { 0.01, -0.02 }, new[] { 1e6, 2e6 }, 252);
        // mean(|0.01|/1e6, |0.02|/2e6) * 1e6 = mean(0.01, 0.01) = 0.01
        SurfaceMetricRegistry.Amihud(s).Should().BeApproximately(0.01, 1e-12);
    }

    [Fact]
    public void Registry_HasNoPortfolioMetrics()
    {
        // The portfolio/backtest layer was removed by design — the registry must stay pure
        // return statistics (no sharpe/winrate/profitfactor/maxdd/stop-loss style entries).
        var ids = SurfaceMetricRegistry.All.Select(m => m.Id).ToArray();
        ids.Should().NotContain(new[] { "sharpe", "sortino", "calmar", "totalreturn", "cagr",
            "maxdd", "ulcer", "winrate", "profitfactor", "expectancy", "beta", "pearson" });
    }

    [Fact]
    public void EmptySample_MetricsAreNaN()
    {
        var s = SurfaceCellSample.Empty;
        foreach (var m in SurfaceMetricRegistry.All)
        {
            var v = m.Compute(s);
            (double.IsNaN(v) || v == 0.0).Should().BeTrue(
                $"metric '{m.Id}' must yield NaN (or 0 for counts) on an empty sample, got {v}");
        }
    }
}

/// <summary>Formula-bar parser (Core/Quant/Surfaces).</summary>
public sealed class SurfaceFormulaParserTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]        // right-associative
    [InlineData("-2 ^ 2", -4)]            // ^ binds tighter than unary minus (math convention)
    [InlineData("2 ^ -2", 0.25)]          // unary allowed on the exponent
    [InlineData("10 / 4", 2.5)]
    public void Arithmetic_And_Precedence(string text, double expected)
    {
        var f = SurfaceFormula.TryParse(text, out var error);
        error.Should().BeNull();
        f!.Evaluate(_ => 0).Should().BeApproximately(expected, 1e-12);
    }

    [Theory]
    [InlineData("Sqrt(16)", 4)]
    [InlineData("Abs(-3)", 3)]
    [InlineData("Log(Exp(2))", 2)]
    [InlineData("Max(1, 5, 3)", 5)]
    [InlineData("Min(4, 2)", 2)]
    [InlineData("Avg(1, 2, 3, 4)", 2.5)]
    [InlineData("Sum(1, 2, 3)", 6)]
    public void Functions_Evaluate(string text, double expected)
    {
        var f = SurfaceFormula.TryParse(text, out var error);
        error.Should().BeNull();
        f!.Evaluate(_ => 0).Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void Variables_ResolveThroughCallback()
    {
        var f = SurfaceFormula.TryParse("zscore / (1 + Abs(var95))", out var error);
        error.Should().BeNull();
        f!.Variables.Should().BeEquivalentTo(new[] { "zscore", "var95" });
        f.Evaluate(id => id == "zscore" ? 2.0 : 0.25).Should().BeApproximately(1.6, 1e-12);
    }

    [Fact]
    public void UnknownVariable_IsRejectedAtParseTime()
    {
        SurfaceFormula.TryParse("bogusmetric + 1", out var error).Should().BeNull();
        error.Should().Contain("bogusmetric");
    }

    [Fact]
    public void RemovedPortfolioVariable_IsRejected()
    {
        SurfaceFormula.TryParse("sharpe * 2", out var error).Should().BeNull();
        error.Should().Contain("sharpe");
    }

    [Fact]
    public void UnknownFunction_IsRejected()
    {
        SurfaceFormula.TryParse("Tan(1)", out var error).Should().BeNull();
        error.Should().Contain("Unknown function");
    }

    [Fact]
    public void TrailingGarbage_IsRejected()
    {
        SurfaceFormula.TryParse("1 + 2 )", out var error).Should().BeNull();
        error.Should().NotBeNull();
    }
}

/// <summary>Grid post-processing: peak finding + robustness scoring + slices.</summary>
public sealed class SurfaceGridAnalysisTests
{
    [Fact]
    public void FindMax_IgnoresNaN()
    {
        var z = new[,] { { 1.0, double.NaN }, { 3.0, 2.0 } };
        var max = SurfaceGridAnalysis.FindMax(z);
        (max.Row, max.Col, max.Value).Should().Be((1, 0, 3.0));
    }

    [Fact]
    public void Robustness_SpikeScoresHigherThanPlateau()
    {
        var z = new double[5, 5];
        z[2, 2] = 10;
        var rob = SurfaceGridAnalysis.Robustness(z);
        rob[2, 2].Should().BeGreaterThan(rob[0, 0]);
        rob[2, 2].Should().BeGreaterThan(0.5);
        rob[0, 0].Should().Be(0);
    }

    [Fact]
    public void Slices_ExtractRowAndColumn()
    {
        var z = new[,] { { 1.0, 2.0 }, { 3.0, 4.0 } };
        SurfaceGridAnalysis.SliceAtRow(z, 1).Should().Equal(3.0, 4.0);
        SurfaceGridAnalysis.SliceAtColumn(z, 1).Should().Equal(2.0, 4.0);
    }
}

/// <summary>End-to-end grid building over synthetic bars (both modes).</summary>
public sealed class SurfaceGridBuilderTests
{
    private static List<Bar> HourlyBars(int count, Func<int, double> ret)
    {
        var bars = new List<Bar>(count);
        var t = new DateTime(2025, 1, 6, 0, 0, 0, DateTimeKind.Utc); // a Monday
        var close = 100.0;
        for (var i = 0; i < count; i++)
        {
            close *= 1 + ret(i);
            bars.Add(new Bar(t, close * 0.999, close * 1.001, close * 0.998, close, 1000 + i % 50));
            t = t.AddHours(1);
        }
        return bars;
    }

    [Fact]
    public void EstimatePeriodsPerYear_HourlyBars()
    {
        var bars = HourlyBars(500, _ => 0.001);
        SurfaceGridBuilder.EstimatePeriodsPerYear(bars).Should().BeApproximately(8760, 1);
    }

    [Fact]
    public void TemporalMode_RecoversAPlantedHourEffect()
    {
        // Returns are +1% only at hour 10, ~0 otherwise.
        var bars = HourlyBars(24 * 60, i => 0);
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i].TimestampUtc.Hour != 10) continue;
            var prev = bars[i - 1].Close;
            bars[i] = bars[i] with { Close = prev * 1.01 };
        }

        var request = new SurfaceRequest(
            SurfaceMode.TemporalAggregation,
            new SurfaceAxisSpec("hour", 0, 23, 1),
            new SurfaceAxisSpec("dow", 0, 6, 1),
            new SurfaceAxisSpec("avgret", 0, 0, 0),
            new SurfaceAxisSpec("count", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.Columns.Should().Be(24);
        result.Rows.Should().Be(7);

        for (var row = 0; row < result.Rows; row++)
        {
            if (double.IsNaN(result.Z[row, 10])) continue;
            result.Z[row, 10].Should().BeGreaterThan(0.005);
        }
    }

    [Fact]
    public void TemporalMode_FormulaOverridesMetric()
    {
        var bars = HourlyBars(24 * 30, i => Math.Sin(i / 10.0) * 0.01);
        var request = new SurfaceRequest(
            SurfaceMode.TemporalAggregation,
            new SurfaceAxisSpec("hour", 0, 23, 1),
            new SurfaceAxisSpec("dow", 0, 6, 1),
            new SurfaceAxisSpec("avgret", 0, 0, 0, Formula: "avgret / (1 + Abs(stdret))"),
            new SurfaceAxisSpec("count", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.ZName.Should().Contain("avgret");

        // Spot-check a populated cell against the direct computation of the same bucket.
        for (var r = 0; r < result.Rows; r++)
        {
            for (var c = 0; c < result.Columns; c++)
            {
                if (double.IsNaN(result.Z[r, c])) continue;
                var bucket = new List<double>();
                for (var i = 1; i < bars.Count; i++)
                {
                    var t = bars[i].TimestampUtc;
                    if (t.Hour == c && ((int)t.DayOfWeek + 6) % 7 == r)
                        bucket.Add(bars[i].Close / bars[i - 1].Close - 1);
                }
                var sample = new SurfaceCellSample(bucket.ToArray(), null, 8760);
                var expected = SurfaceMetricRegistry.Mean(sample) / (1 + Math.Abs(SurfaceMetricRegistry.StdDev(sample)));
                result.Z[r, c].Should().BeApproximately(expected, 1e-12);
                return; // one populated cell is enough
            }
        }
        Assert.Fail("no populated cell found");
    }

    [Fact]
    public void CrossSectionalMode_BucketsByPriorReturnAndVolume()
    {
        var rng = new Random(42);
        var bars = HourlyBars(3000, _ => (rng.NextDouble() - 0.5) * 0.02);
        var request = new SurfaceRequest(
            SurfaceMode.CrossSectional,
            new SurfaceAxisSpec("retbin", -0.02, 0.02, 0.01),
            new SurfaceAxisSpec("voldecile", 1, 10, 1),
            new SurfaceAxisSpec("avgret", 0, 0, 0),
            new SurfaceAxisSpec("count", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.Columns.Should().Be(4); // (-0.02..0.02)/0.01 = 4 bins
        result.Rows.Should().Be(10);
        var total = 0.0;
        foreach (var w in result.W)
            if (!double.IsNaN(w)) total += w;
        total.Should().BeGreaterThan(0).And.BeLessThan(3000);
    }

    [Fact]
    public void CrossSectionalMode_LagAxis()
    {
        var rng = new Random(7);
        var bars = HourlyBars(2000, _ => (rng.NextDouble() - 0.5) * 0.02);
        var request = new SurfaceRequest(
            SurfaceMode.CrossSectional,
            new SurfaceAxisSpec("retbin", -0.02, 0.02, 0.01),
            new SurfaceAxisSpec("lag", 1, 5, 1),
            new SurfaceAxisSpec("probup", 0, 0, 0),
            new SurfaceAxisSpec("count", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.Rows.Should().Be(5);
        result.YLabels[0].Should().StartWith("t").And.EndWith("1");
        for (var r = 0; r < result.Rows; r++)
        {
            for (var c = 0; c < result.Columns; c++)
            {
                if (double.IsNaN(result.Z[r, c])) continue;
                result.Z[r, c].Should().BeInRange(0.2, 0.8);
            }
        }
    }

    [Fact]
    public void BadFormula_FailsFast()
    {
        var bars = HourlyBars(300, _ => 0.001);
        var request = new SurfaceRequest(
            SurfaceMode.TemporalAggregation,
            new SurfaceAxisSpec("hour", 0, 23, 1),
            new SurfaceAxisSpec("dow", 0, 6, 1),
            new SurfaceAxisSpec("avgret", 0, 0, 0, Formula: "notametric * 2"),
            new SurfaceAxisSpec("count", 0, 0, 0),
            8760);

        var act = () => SurfaceGridBuilder.Build(bars, request);
        act.Should().Throw<ArgumentException>().WithMessage("*notametric*");
    }
}

/// <summary>The live rolling bar window feeding the streaming Surface Lab.</summary>
public sealed class LiveBarSeriesTests
{
    private static readonly DateTime T0 = new(2025, 6, 2, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PricesAggregateIntoBucketedOhlcBars()
    {
        var series = new LiveBarSeries(TimeSpan.FromMinutes(1), 100);
        series.PushPrice(T0.AddSeconds(1), 100);
        series.PushPrice(T0.AddSeconds(20), 102);   // high
        series.PushPrice(T0.AddSeconds(40), 99);    // low
        series.PushPrice(T0.AddSeconds(59), 101);   // close
        series.PushVolume(500);
        series.PushPrice(T0.AddSeconds(61), 103);   // next bucket → commits the first bar

        var bars = series.Snapshot();
        bars.Should().HaveCount(2);
        bars[0].TimestampUtc.Should().Be(T0);
        (bars[0].Open, bars[0].High, bars[0].Low, bars[0].Close).Should().Be((100.0, 102.0, 99.0, 101.0));
        bars[0].Volume.Should().Be(500);
        bars[1].Open.Should().Be(103);
    }

    [Fact]
    public void RetentionIsHardCapped()
    {
        var series = new LiveBarSeries(TimeSpan.FromMinutes(1), 10);
        for (var i = 0; i < 500; i++)
            series.PushPrice(T0.AddMinutes(i), 100 + i * 0.01);
        series.Count.Should().BeLessThanOrEqualTo(11); // cap + forming bar
    }

    [Fact]
    public void SeededHistoryAndLiveTailNeverOverlap()
    {
        var history = new List<Bar>
        {
            new(T0, 100, 101, 99, 100.5, 10),
            new(T0.AddMinutes(1), 100.5, 101.5, 100, 101, 12),
        };
        var series = new LiveBarSeries(TimeSpan.FromMinutes(1), 100);
        series.Seed(history);

        // A live tick INSIDE the last seeded bar's bucket must not rewind time.
        series.PushPrice(T0.AddMinutes(1).AddSeconds(30), 105);
        var bars = series.Snapshot();
        bars.Should().HaveCount(3);
        bars[2].TimestampUtc.Should().Be(T0.AddMinutes(2));
        bars[2].Close.Should().Be(105);
        bars.Should().BeInAscendingOrder(b => b.TimestampUtc);
    }

    [Fact]
    public void SnapshotIsAPointInTimeCopy()
    {
        var series = new LiveBarSeries(TimeSpan.FromMinutes(1), 100);
        series.PushPrice(T0, 100);
        var snap = series.Snapshot();
        series.PushPrice(T0.AddSeconds(10), 200);
        snap[0].Close.Should().Be(100); // later pushes don't mutate the handed-out copy
    }
}
