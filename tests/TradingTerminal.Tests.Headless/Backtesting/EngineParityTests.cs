using System.IO;
using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using OldBacktestConfig = TradingTerminal.Core.Backtest.BacktestConfig;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>
/// Parity gate: the new <see cref="BacktestEngine"/> must reproduce the old
/// <see cref="BacktestSession"/> to floating-point exactness when running the SAME strategy over the
/// SAME ticks. We run the legacy <see cref="MeanReversionStrategy"/> natively on the old engine and,
/// via <see cref="BacktestStrategyKernelAdapter"/>, on the new engine, then compare the common
/// round-trip ledger. The canonical engine additionally fills the strategy's final liquidation at
/// the last touch; the legacy session leaves that order working and marks the position at mid.
/// Slippage 0 + multiplier 1 keeps the shared replay on identical arithmetic.
/// </summary>
public sealed class EngineParityTests
{
    private static readonly InstrumentId Id = new(1);
    private static readonly Contract TestContract = Contract.UsStock("TEST");

    private const int LookbackTicks = 30;
    private const double EntryThreshold = 0.5;
    private const double StopThreshold = 10.0;
    private const long Quantity = 5;
    private const double StartingCash = 100_000d;
    private const double TickSize = 0.01;

    private static List<Tick> SyntheticTicks(int count)
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ticks = new List<Tick>(count);
        for (var i = 0; i < count; i++)
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0) + 0.5 * Math.Sin(i * 2 * Math.PI / 13.0);
            ticks.Add(new Tick(start.AddSeconds(i), Bid: mid - 0.01, Ask: mid + 0.01, BidSize: 10, AskSize: 10));
        }
        return ticks;
    }

    private static MeanReversionStrategy NewStrategy() =>
        new(TestContract, LookbackTicks, EntryThreshold, StopThreshold, Quantity);

    [Fact]
    public async Task New_engine_matches_old_engine_on_the_same_strategy_and_ticks()
    {
        var ticks = SyntheticTicks(800);

        // --- Old engine: replay from a temp parquet (its only in-test data path). ---
        var parquetPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.parquet");
        try
        {
            await using (var writer = new ParquetTickWriter(parquetPath))
                foreach (var t in ticks)
                    await writer.WriteAsync(t);

            var oldConfig = new OldBacktestConfig(
                Contract: TestContract,
                TickDataPath: parquetPath,
                TickSize: TickSize,
                SlippageTicks: 0,
                ContractMultiplier: 1.0,
                StartingCash: StartingCash);

            var oldResult = await new BacktestSession().RunAsync(oldConfig, NewStrategy(), risk: null);

            // --- New engine: same ticks in memory, same strategy via the adapter. ---
            var spec = new RunSpec(
                Universe: Universe.Single(new InstrumentSpec(Id, TestContract, TickSize, ContractMultiplier: 1.0)),
                Data: new DataSpec(),
                StartingCash: StartingCash);

            var feed = new InMemoryMarketDataFeed(ticks.Select(t => MarketEvent.OfQuote(Id, t)));
            var newReport = await new BacktestEngine(feed)
                .RunAsync(spec, new BacktestStrategyKernelAdapter(NewStrategy()));

            // --- Parity assertions. ---
            newReport.Trades.Should().NotBeEmpty("the strategy must actually trade for parity to be meaningful");
            oldResult.Fills.Should().NotBeNull();
            var legacyFills = oldResult.Fills!;
            var legacyOpenQuantity = legacyFills.Sum(f =>
                f.Side == TradingTerminal.Core.Trading.OrderSide.Buy ? f.Quantity : -f.Quantity);
            var expectedFinalCloseCount = legacyOpenQuantity == 0 ? 0 : 1;

            newReport.Trades.Count.Should().Be(oldResult.Trades.Count + expectedFinalCloseCount);
            newReport.Trades.Take(oldResult.Trades.Count).Sum(t => t.GrossPnl)
                .Should().BeApproximately(oldResult.Trades.Sum(t => t.GrossPnl), 1e-6);

            for (var i = 0; i < oldResult.Trades.Count; i++)
            {
                newReport.Trades[i].EntryPrice.Should().BeApproximately(oldResult.Trades[i].EntryPrice, 1e-9);
                newReport.Trades[i].ExitPrice.Should().BeApproximately(oldResult.Trades[i].ExitPrice, 1e-9);
                newReport.Trades[i].GrossPnl.Should().BeApproximately(oldResult.Trades[i].GrossPnl, 1e-9);
            }

            if (legacyOpenQuantity == 0)
            {
                newReport.Summary.EndingEquity.Should().BeApproximately(oldResult.EndingCash, 1e-6);
            }
            else
            {
                var last = ticks[^1];
                var expectedExit = legacyOpenQuantity > 0 ? last.Bid : last.Ask;
                var expectedSpreadCost = Math.Abs(legacyOpenQuantity) * (last.Ask - last.Bid) * 0.5;
                var finalClose = newReport.Trades[^1];

                finalClose.Quantity.Should().Be(Math.Abs(legacyOpenQuantity));
                finalClose.ExitPrice.Should().BeApproximately(expectedExit, 1e-9);
                finalClose.ExitUtc.Should().Be(last.TimestampUtc);
                newReport.Summary.EndingEquity
                    .Should().BeApproximately(oldResult.EndingCash - expectedSpreadCost, 1e-6);
            }
        }
        finally
        {
            if (File.Exists(parquetPath)) File.Delete(parquetPath);
        }
    }
}
