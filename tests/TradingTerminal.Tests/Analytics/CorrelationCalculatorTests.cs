using FluentAssertions;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Analytics;

public sealed class CorrelationCalculatorTests
{
    private static Bar BarAt(DateTime t, double close) => new(t, close, close, close, close, 0);

    private static IReadOnlyList<Bar> Series(DateTime start, params double[] closes)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < closes.Length; i++)
            bars.Add(BarAt(start.AddDays(i), closes[i]));
        return bars;
    }

    [Fact]
    public void LogReturns_has_length_n_minus_1()
    {
        var r = CorrelationCalculator.LogReturns(new double[] { 100, 110, 121 });
        r.Should().HaveCount(2);
        r[0].Should().BeApproximately(Math.Log(1.1), 1e-12);
    }

    [Fact]
    public void LogReturns_short_or_null_series_is_empty()
    {
        CorrelationCalculator.LogReturns(new double[] { 100 }).Should().BeEmpty();
        CorrelationCalculator.LogReturns(Array.Empty<double>()).Should().BeEmpty();
    }

    [Fact]
    public void Pearson_identical_series_is_one()
    {
        var a = new double[] { 0.01, -0.02, 0.03, 0.00, 0.015 };
        CorrelationCalculator.Pearson(a, a).Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Pearson_perfectly_inverse_series_is_minus_one()
    {
        var a = new double[] { 0.01, -0.02, 0.03, 0.00, 0.015 };
        var b = a.Select(x => -x).ToArray();
        CorrelationCalculator.Pearson(a, b).Should().BeApproximately(-1.0, 1e-12);
    }

    [Fact]
    public void Pearson_zero_variance_series_is_zero_not_nan()
    {
        var flat = new double[] { 0.0, 0.0, 0.0, 0.0 };
        var moving = new double[] { 0.01, -0.01, 0.02, 0.00 };
        double r = CorrelationCalculator.Pearson(flat, moving);
        r.Should().Be(0.0);
        double.IsNaN(r).Should().BeFalse();
    }

    [Fact]
    public void PearsonMatrix_diagonal_is_one_and_symmetric()
    {
        var s1 = new double[] { 0.01, -0.02, 0.03, 0.00, 0.015 };
        var s2 = new double[] { 0.02, -0.01, 0.04, 0.01, 0.020 };
        var s3 = s1.Select(x => -x).ToArray();
        var m = CorrelationCalculator.PearsonMatrix(new[] { s1, s2, s3 });

        m[0, 0].Should().Be(1.0);
        m[1, 1].Should().Be(1.0);
        m[2, 2].Should().Be(1.0);
        m[0, 1].Should().BeApproximately(m[1, 0], 1e-12);
        m[0, 2].Should().BeApproximately(-1.0, 1e-12); // s3 = -s1
    }

    [Fact]
    public void AlignByTimestamp_keeps_only_common_timestamps()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // a: days 0..3 ; b: days 1..4 → common = days 1,2,3
        var a = Series(start, 100, 101, 102, 103);
        var b = Series(start.AddDays(1), 200, 198, 202, 205);

        var (timestamps, aligned) = CorrelationCalculator.AlignByTimestamp(new[] { a, b });

        timestamps.Should().HaveCount(3);
        timestamps[0].Should().Be(start.AddDays(1));
        aligned[0].Should().Equal(101, 102, 103);
        aligned[1].Should().Equal(200, 198, 202);
    }

    [Fact]
    public void AlignByTimestamp_duplicate_timestamp_keeps_last_close()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new List<Bar> { BarAt(start, 100), BarAt(start, 105), BarAt(start.AddDays(1), 110) };
        var b = Series(start, 200, 210);

        var (timestamps, aligned) = CorrelationCalculator.AlignByTimestamp(new IReadOnlyList<Bar>[] { a, b });

        timestamps.Should().HaveCount(2);
        aligned[0][0].Should().Be(105); // duplicate at `start` → last wins
    }
}
