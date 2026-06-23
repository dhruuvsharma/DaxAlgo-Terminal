using FluentAssertions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

public sealed class StatisticsCalculatorTests
{
    [Fact]
    public void MaxDrawdown_PeakToTrough_IsExpressedAsFractionOfPeak()
    {
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curve = new[]
        {
            new EquityPoint(origin.AddMinutes(0),  10_000),
            new EquityPoint(origin.AddMinutes(1),  11_000), // peak
            new EquityPoint(origin.AddMinutes(2),   9_900), // 10% drop from peak
            new EquityPoint(origin.AddMinutes(3),  10_500),
        };
        var result = new BacktestResult(Array.Empty<Trade>(), curve, 10_000, 10_500);

        var stats = StatisticsCalculator.Calculate(result);

        stats.MaxDrawdown.Should().BeApproximately(0.10, 1e-9);
        stats.TotalReturn.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void TradeStats_WinRateProfitFactorExpectancy_MatchManualMath()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Trade Win(double pnl) => new(t0, t0.AddSeconds(1), OrderSide.Buy, 1, 100, 100 + pnl, pnl);
        Trade Loss(double pnl) => new(t0, t0.AddSeconds(1), OrderSide.Buy, 1, 100, 100 + pnl, pnl);

        var trades = new[] { Win(20), Win(10), Loss(-5), Loss(-15) };
        var result = new BacktestResult(trades, Array.Empty<EquityPoint>(), 10_000, 10_010);

        var stats = StatisticsCalculator.Calculate(result);

        stats.TradeCount.Should().Be(4);
        stats.WinRate.Should().BeApproximately(0.5, 1e-9);
        stats.AvgWin.Should().BeApproximately(15, 1e-9);
        stats.AvgLoss.Should().BeApproximately(-10, 1e-9);
        stats.ProfitFactor.Should().BeApproximately(30.0 / 20.0, 1e-9);
        stats.Expectancy.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Sharpe_FlatEquity_IsZero()
    {
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var curve = Enumerable.Range(0, 100)
            .Select(i => new EquityPoint(origin.AddMinutes(i), 10_000))
            .ToArray();
        var result = new BacktestResult(Array.Empty<Trade>(), curve, 10_000, 10_000);

        var stats = StatisticsCalculator.Calculate(result);

        stats.Sharpe.Should().Be(0);
        stats.Sortino.Should().Be(0);
        stats.MaxDrawdown.Should().Be(0);
    }

    [Fact]
    public void Sharpe_PositiveTrend_IsPositive_AndAnnualized()
    {
        // Steady 0.01% return per minute → mean=0.0001, std=0 → Sharpe is technically undefined,
        // so jitter the curve slightly to make std > 0.
        var origin = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rng = new Random(42);
        var curve = new List<EquityPoint>();
        var equity = 10_000d;
        for (var i = 0; i < 1_000; i++)
        {
            equity *= 1 + 0.0001 + (rng.NextDouble() - 0.5) * 0.0001;
            curve.Add(new EquityPoint(origin.AddMinutes(i), equity));
        }
        var result = new BacktestResult(Array.Empty<Trade>(), curve, 10_000, curve[^1].Equity);

        var stats = StatisticsCalculator.Calculate(result);

        stats.Sharpe.Should().BeGreaterThan(0);
        // 1-minute samples, drift swamps jitter → Sharpe should be very high.
        stats.Sharpe.Should().BeGreaterThan(5);
    }
}
