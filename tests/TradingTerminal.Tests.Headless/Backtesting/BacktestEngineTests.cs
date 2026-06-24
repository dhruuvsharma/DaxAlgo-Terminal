using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>
/// End-to-end smoke + accounting checks for the new <see cref="BacktestEngine"/> (P1). Drives an
/// oscillating synthetic quote series through the reference <see cref="MeanReversionKernel"/> and
/// asserts the run produces trades, an equity timeline, and a populated metric set.
/// </summary>
public sealed class BacktestEngineTests
{
    private static readonly InstrumentId Id = new(1);

    private static RunSpec MeanReversionSpec() => new(
        Universe: Universe.Single(new InstrumentSpec(Id, Contract.UsStock("TEST"), TickSize: 0.01, ContractMultiplier: 1.0)),
        Data: new DataSpec(),
        StrategyId: "meanReversion",
        Parameters: new StrategyParameters(new Dictionary<string, double>
        {
            ["lookback"] = 20,
            ["entryZ"] = 1.0,
            ["exitZ"] = 0.2,
            ["qty"] = 10,
        }),
        StartingCash: 100_000d);

    private static IEnumerable<MarketEvent> OscillatingQuotes(int count)
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0);
            var tick = new Tick(start.AddSeconds(i), Bid: mid - 0.01, Ask: mid + 0.01, BidSize: 10, AskSize: 10);
            yield return MarketEvent.OfQuote(Id, tick);
        }
    }

    [Fact]
    public async Task Run_over_oscillating_quotes_produces_trades_and_metrics()
    {
        var feed = new InMemoryMarketDataFeed(OscillatingQuotes(500));
        var engine = new BacktestEngine(feed);

        var report = await engine.RunAsync(MeanReversionSpec(), new MeanReversionKernel());

        report.Summary.EventsProcessed.Should().Be(500);
        report.Equity.Should().NotBeEmpty();
        report.Trades.Should().NotBeEmpty("an oscillating series should trip the z-score thresholds");
        report.Metrics.Has(MetricSet.Keys.Sharpe).Should().BeTrue();
        report.Summary.EndingEquity.Should().NotBe(double.NaN);
        report.PerInstrument.Should().ContainSingle().Which.Instrument.Should().Be(Id);
    }

    [Fact]
    public async Task Flat_strategy_conserves_cash_and_reports_zero_trades()
    {
        var feed = new InMemoryMarketDataFeed(OscillatingQuotes(100));
        var engine = new BacktestEngine(feed);

        // A kernel that never trades must leave equity exactly at starting cash.
        var report = await engine.RunAsync(MeanReversionSpec(), new NoopKernel());

        report.Trades.Should().BeEmpty();
        report.Summary.EndingEquity.Should().Be(100_000d);
        report.Summary.NetProfit.Should().Be(0d);
    }

    private sealed class NoopKernel : IStrategyKernel
    {
        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
