using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Cutover check: a legacy <c>IBacktestStrategy</c> (the shape the 12 shipped strategies use)
/// runs unchanged on the new engine through the contract-deferred
/// <see cref="BacktestStrategyKernelAdapter"/> — the bridge that lets the Studio catalog them.</summary>
public sealed class LegacyBridgeTests
{
    private static readonly InstrumentId Id = new(1);

    [Fact]
    public async Task Legacy_strategy_runs_on_new_engine_via_deferred_adapter()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Enumerable.Range(0, 1000).Select(i =>
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0);
            return MarketEvent.OfQuote(Id, new Tick(start.AddSeconds(i), mid - 0.01, mid + 0.01, 10, 10));
        });

        var spec = new RunSpec(
            Universe.Single(new InstrumentSpec(Id, Contract.UsStock("TEST"), 0.01, 1.0)),
            new DataSpec());

        // Deferred build: the adapter constructs the legacy strategy from the run's instrument.
        var kernel = new BacktestStrategyKernelAdapter(
            contract => new MeanReversionStrategy(contract, lookbackTicks: 30, entryThreshold: 0.5, stopThreshold: 10, quantity: 5));

        var report = await new BacktestEngine(new InMemoryMarketDataFeed(events)).RunAsync(spec, kernel);

        report.Summary.EventsProcessed.Should().Be(1000);
        report.Trades.Should().NotBeEmpty();
    }
}
