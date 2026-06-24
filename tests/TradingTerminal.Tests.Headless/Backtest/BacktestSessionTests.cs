using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

public sealed class BacktestSessionTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"daxalgo-bt-{Guid.NewGuid():N}.parquet");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public async Task BuyThenSell_ProducesOneTrade_WithExpectedPnl()
    {
        // Synthetic ticks: 200 ticks, monotonically rising mid from 100.00 to 100.99 in 0.005 steps.
        var origin = new DateTime(2026, 5, 12, 14, 0, 0, DateTimeKind.Utc);
        await using (var writer = new ParquetTickWriter(_tempPath, rowGroupSize: 64))
        {
            for (var i = 0; i < 200; i++)
            {
                var mid = 100.00 + i * 0.005;
                await writer.WriteAsync(new Tick(
                    origin.AddSeconds(i),
                    Bid: mid - 0.005,
                    Ask: mid + 0.005,
                    BidSize: 10,
                    AskSize: 10));
            }
        }

        var strategy = new BuyOnTick10SellOnTick100();
        var config = new BacktestConfig(
            Contract: Contract.UsStock("TEST"),
            TickDataPath: _tempPath,
            TickSize: 0.01,
            SlippageTicks: 0,
            ContractMultiplier: 1.0,
            StartingCash: 10_000);

        var session = new BacktestSession();
        var result = await session.RunAsync(config, strategy);

        result.Trades.Should().HaveCount(1);
        var trade = result.Trades[0];
        trade.Side.Should().Be(OrderSide.Buy);
        trade.Quantity.Should().Be(1);
        // Tick 10: mid 100.05, ask 100.055 → buy
        // Tick 100: mid 100.50, bid 100.495 → sell
        trade.EntryPrice.Should().BeApproximately(100.055, 1e-9);
        trade.ExitPrice.Should().BeApproximately(100.495, 1e-9);
        trade.GrossPnl.Should().BeApproximately(0.440, 1e-9);

        result.EquityCurve.Should().NotBeEmpty();
        result.EndingCash.Should().BeApproximately(10_000.440, 1e-6);
    }

    private sealed class BuyOnTick10SellOnTick100 : IBacktestStrategy
    {
        private int _ticks;
        private bool _bought;
        private bool _sold;

        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

        public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
        {
            _ticks++;
            if (_ticks == 10 && !_bought)
            {
                _bought = true;
                await router.PlaceOrderAsync(new OrderRequest(
                    ClientOrderId: "buy-1",
                    Contract: Contract.UsStock("TEST"),
                    Side: OrderSide.Buy,
                    Type: OrderType.Market,
                    Quantity: 1), ct);
            }
            else if (_ticks == 100 && !_sold)
            {
                _sold = true;
                await router.PlaceOrderAsync(new OrderRequest(
                    ClientOrderId: "sell-1",
                    Contract: Contract.UsStock("TEST"),
                    Side: OrderSide.Sell,
                    Type: OrderType.Market,
                    Quantity: 1), ct);
            }
        }

        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    }
}
