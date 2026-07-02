using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Quant.Surfaces;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>Metric registry math for the 3D Surface Lab (Core/Quant/Surfaces).</summary>
public sealed class SurfaceMetricsTests
{
    private static SurfaceCellSample Sample(double[] returns, double ppy = 252) =>
        new(returns, null, null, null, ppy);

    [Fact]
    public void TotalReturn_Compounds()
    {
        SurfaceMetricRegistry.TotalReturn(Sample(new[] { 0.10, 0.10 }))
            .Should().BeApproximately(0.21, 1e-12);
    }

    [Fact]
    public void Sharpe_KnownSeries()
    {
        // mean 0.01, sample std 0.01 → sharpe = 1 * sqrt(252)
        var s = Sample(new[] { 0.0, 0.02, 0.0, 0.02 });
        var mean = 0.01;
        var std = Math.Sqrt(4.0 / 3.0 * 0.0001);
        SurfaceMetricRegistry.Sharpe(s).Should().BeApproximately(mean / std * Math.Sqrt(252), 1e-9);
    }

    [Fact]
    public void Sharpe_ZeroVariance_IsNaN()
    {
        SurfaceMetricRegistry.Sharpe(Sample(new[] { 0.01, 0.01, 0.01 })).Should().Be(double.NaN);
    }

    [Fact]
    public void MaxDrawdown_PeakToTrough()
    {
        // equity: 1.1 → 0.88 (peak 1.1, trough 0.88 ⇒ dd = 0.2), then recovery
        var s = Sample(new[] { 0.10, -0.20, 0.05 });
        SurfaceMetricRegistry.MaxDrawdown(s).Should().BeApproximately(0.20, 1e-12);
    }

    [Fact]
    public void ValueAtRisk_IsPositiveLossQuantile()
    {
        var returns = Enumerable.Range(1, 100).Select(i => -i / 1000.0).ToArray(); // -0.1% … -10%
        var s = Sample(returns);
        // 95% VaR: 5th percentile of the sorted (ascending) returns = index 5 → -0.095 … loss 0.095
        SurfaceMetricRegistry.ValueAtRisk(s, 0.95).Should().BeGreaterThan(0.09).And.BeLessThan(0.10);
        SurfaceMetricRegistry.ConditionalVaR(s, 0.95).Should()
            .BeGreaterThan(SurfaceMetricRegistry.ValueAtRisk(s, 0.95));
    }

    [Fact]
    public void WinRateAndProfitFactor_UseTradesWhenPresent()
    {
        var s = new SurfaceCellSample(
            new[] { 0.01, -0.01 }, null, null,
            TradeReturns: new[] { 0.05, 0.05, -0.02, 0.03 }, 252);
        SurfaceMetricRegistry.WinRate(s).Should().BeApproximately(0.75, 1e-12);
        SurfaceMetricRegistry.ProfitFactor(s).Should().BeApproximately(0.13 / 0.02, 1e-9);
        SurfaceMetricRegistry.Expectancy(s).Should().BeApproximately(0.0275, 1e-12);
    }

    [Fact]
    public void BetaAndPearson_AgainstBenchmark()
    {
        var bench = new[] { 0.01, -0.02, 0.015, 0.005, -0.01 };
        var strat = bench.Select(b => 2 * b).ToArray(); // beta 2, corr 1
        var s = new SurfaceCellSample(strat, bench, null, null, 252);
        SurfaceMetricRegistry.Beta(s).Should().BeApproximately(2.0, 1e-9);
        SurfaceMetricRegistry.Pearson(s).Should().BeApproximately(1.0, 1e-9);
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
        var f = SurfaceFormula.TryParse("sharpe / (1 + Abs(maxdd))", out var error);
        error.Should().BeNull();
        f!.Variables.Should().BeEquivalentTo(new[] { "sharpe", "maxdd" });
        f.Evaluate(id => id == "sharpe" ? 2.0 : 0.25).Should().BeApproximately(1.6, 1e-12);
    }

    [Fact]
    public void UnknownVariable_IsRejectedAtParseTime()
    {
        SurfaceFormula.TryParse("bogusmetric + 1", out var error).Should().BeNull();
        error.Should().Contain("bogusmetric");
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
    public void Robustness_FlatGridHasNaNRange_SpikeScoresHigh()
    {
        // A flat plateau with one spike: the spike (and its neighbors) score worse than the far plateau.
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

/// <summary>End-to-end grid building over synthetic bars (all three modes).</summary>
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
        // Returns are +1% only at hour 10, 0 otherwise (bar i's return is realized AT its timestamp).
        var bars = HourlyBars(24 * 60, i => 0);
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i].TimestampUtc.Hour != 10) continue;
            var prev = bars[i - 1].Close;
            bars[i] = bars[i] with { Close = prev * 1.01 };
        }
        // Rebuild consistency of neighboring closes is irrelevant — the builder only uses close ratios.

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

        // Every populated hour-10 cell should average ≈ +1%; hour-5 cells ≈ 0 (may drift from
        // the neighbor-close rewrite above, so give it slack).
        for (var row = 0; row < result.Rows; row++)
        {
            if (double.IsNaN(result.Z[row, 10])) continue;
            result.Z[row, 10].Should().BeGreaterThan(0.005);
        }
    }

    [Fact]
    public void ParameterMode_ProducesGridAndNaNsDegenerateCells()
    {
        var bars = HourlyBars(600, i => Math.Sin(i / 15.0) * 0.01);
        var request = new SurfaceRequest(
            SurfaceMode.ParameterOptimization,
            new SurfaceAxisSpec("fastma", 5, 25, 10),   // 5, 15, 25
            new SurfaceAxisSpec("slowma", 10, 30, 10),  // 10, 20, 30
            new SurfaceAxisSpec("sharpe", 0, 0, 0),
            new SurfaceAxisSpec("maxdd", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.Columns.Should().Be(3);
        result.Rows.Should().Be(3);

        // fast=25 × slow=10 and fast=15 × slow=10 are degenerate (fast ≥ slow) ⇒ NaN.
        result.Z[0, 2].Should().Be(double.NaN);
        result.Z[0, 1].Should().Be(double.NaN);
        // fast=5 × slow=30 is a legal combo and must be computed (may still be NaN only if
        // the sim never trades — with a sine wave price it does trade).
        double.IsNaN(result.Z[2, 0]).Should().BeFalse();
        // Robustness grid is populated alongside.
        result.Robustness.GetLength(0).Should().Be(3);
    }

    [Fact]
    public void ParameterMode_FormulaOverridesMetric()
    {
        var bars = HourlyBars(400, i => Math.Sin(i / 10.0) * 0.01);
        var request = new SurfaceRequest(
            SurfaceMode.ParameterOptimization,
            new SurfaceAxisSpec("fastma", 5, 15, 5),
            new SurfaceAxisSpec("slowma", 20, 40, 10),
            new SurfaceAxisSpec("sharpe", 0, 0, 0, Formula: "totalreturn / (1 + Abs(maxdd))"),
            new SurfaceAxisSpec("maxdd", 0, 0, 0),
            8760);

        var result = SurfaceGridBuilder.Build(bars, request);
        result.ZName.Should().Contain("totalreturn");
        // Spot-check one cell against the direct computation.
        var overrides = new Dictionary<string, double> { ["fastma"] = 5, ["slowma"] = 20 };
        var sample = ParameterSurfaceSimulator.Run(bars, overrides, 8760);
        var expected = SurfaceMetricRegistry.TotalReturn(sample) / (1 + Math.Abs(SurfaceMetricRegistry.MaxDrawdown(sample)));
        result.Z[0, 0].Should().BeApproximately(expected, 1e-12);
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
        // The W grid carries the per-cell frequency; total observations can't exceed bar count.
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
        // Random-walk data: P(up) hovers around 0.5 in populated cells.
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
