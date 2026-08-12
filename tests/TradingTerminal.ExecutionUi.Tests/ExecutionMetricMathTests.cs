using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

public sealed class ExecutionMetricMathTests
{
    [Fact]
    public void Calculate_DerivesReturnDrawdownWinRateAndBoundedSeries()
    {
        var asOf = new DateTime(2026, 8, 5, 18, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            new ExecutionTradeHistoryPoint(asOf.AddDays(-5), "TEST", 100m),
            new ExecutionTradeHistoryPoint(asOf.AddDays(-3), "TEST", -50m),
            new ExecutionTradeHistoryPoint(asOf.AddDays(-1), "TEST", 50m),
        };

        var result = ExecutionMetricMath.Calculate(
            1_000m,
            history,
            ExecutionTimeRange.SevenDays,
            asOf,
            openPositions: 2,
            netExposure: 250m);

        Assert.Equal(1_100m, result.Metrics.Equity);
        Assert.Equal(100m, result.Metrics.NetProfitAndLoss);
        Assert.Equal(10m, result.Metrics.ReturnPercent);
        Assert.Equal(3, result.Metrics.TradeCount);
        Assert.Equal(2, result.Metrics.WinningTrades);
        Assert.Equal(200m / 3m, result.Metrics.WinRatePercent);
        Assert.InRange(result.Metrics.MaxDrawdownPercent, -4.55m, -4.54m);
        Assert.Equal(2, result.Metrics.OpenPositions);
        Assert.Equal(250m, result.Metrics.NetExposure);
        Assert.Equal(7, result.EquitySeries.Count);
        Assert.Equal(7, result.DailyProfitAndLossSeries.Count);
    }

    [Fact]
    public void MaximumDrawdownPercent_UsesPriorPeak()
    {
        var drawdown = ExecutionMetricMath.MaximumDrawdownPercent([100m, 120m, 90m, 150m, 120m]);

        Assert.Equal(-25m, drawdown);
    }

    [Fact]
    public void AnnualizedSharpe_ReturnsZeroForConstantOrInsufficientSeries()
    {
        Assert.Equal(0d, ExecutionMetricMath.AnnualizedSharpe([0.01]));
        Assert.Equal(0d, ExecutionMetricMath.AnnualizedSharpe([0.01, 0.01, 0.01]));
    }

    [Fact]
    public void Calculate_FiltersTradesToSelectedRangeButCarriesPriorEquity()
    {
        var asOf = new DateTime(2026, 8, 5, 18, 0, 0, DateTimeKind.Utc);
        var history = new[]
        {
            new ExecutionTradeHistoryPoint(asOf.AddDays(-40), "TEST", 200m),
            new ExecutionTradeHistoryPoint(asOf.AddDays(-5), "TEST", -20m),
        };

        var result = ExecutionMetricMath.Calculate(
            1_000m,
            history,
            ExecutionTimeRange.ThirtyDays,
            asOf,
            openPositions: 0,
            netExposure: 0m);

        Assert.Equal(1_180m, result.Metrics.Equity);
        Assert.Equal(-20m, result.Metrics.NetProfitAndLoss);
        Assert.Equal(1, result.Metrics.TradeCount);
        Assert.Equal(-20m / 1_200m * 100m, result.Metrics.ReturnPercent);
    }
}
