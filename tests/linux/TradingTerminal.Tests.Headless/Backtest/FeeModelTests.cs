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

public sealed class FeeModelTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"daxalgo-fee-{Guid.NewGuid():N}.parquet");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Theory]
    [InlineData(LiquidityFlag.Taker, 1.0)]
    [InlineData(LiquidityFlag.Maker, -0.5)]
    public void MakerTakerFeeModel_ChargesOrRebatesPerUnit(LiquidityFlag flag, double expectedPerUnit)
    {
        var model = new MakerTakerFeeModel(takerFeePerUnit: 1.0, makerRebatePerUnit: 0.5);
        model.Fee(OrderSide.Buy, quantity: 10, price: 100.0, flag).Should().Be(expectedPerUnit * 10);
    }

    [Fact]
    public void BpsFeeModel_ScalesWithNotional()
    {
        var model = new BpsFeeModel(1.0); // 1 bp
        model.Fee(OrderSide.Buy, quantity: 100, price: 50.0, LiquidityFlag.Taker)
            .Should().BeApproximately(100 * 50 * 1e-4, 1e-12);
    }

    [Fact]
    public async Task TakerFee_ReducesEndingCash()
    {
        var origin = new DateTime(2026, 5, 12, 14, 0, 0, DateTimeKind.Utc);
        await using (var writer = new ParquetTickWriter(_tempPath, rowGroupSize: 64))
        {
            for (var i = 0; i < 50; i++)
                await writer.WriteAsync(new Tick(origin.AddSeconds(i), 99.99, 100.01, 10, 10));
        }

        var contract = Contract.UsStock("TEST");
        var feeModel = new MakerTakerFeeModel(takerFeePerUnit: 0.10, makerRebatePerUnit: 0);
        var config = new BacktestConfig(
            Contract: contract,
            TickDataPath: _tempPath,
            TickSize: 0.01,
            ContractMultiplier: 1.0,
            StartingCash: 10_000,
            FeeModel: feeModel);

        var session = new BacktestSession();
        var withFees = await session.RunAsync(config, new BuyAndSellOnceStrategy(contract));
        var withoutFees = await session.RunAsync(config with { FeeModel = null }, new BuyAndSellOnceStrategy(contract));

        // Two market fills (taker), 1 share each → 0.20 total taker fees.
        withFees.TotalFees.Should().BeApproximately(0.20, 1e-9);
        withoutFees.TotalFees.Should().Be(0);
        (withoutFees.EndingCash - withFees.EndingCash).Should().BeApproximately(0.20, 1e-9);
    }

    private sealed class BuyAndSellOnceStrategy : IBacktestStrategy
    {
        private readonly Contract _c;
        private int _ticks;
        public BuyAndSellOnceStrategy(Contract c) { _c = c; }
        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
        {
            _ticks++;
            if (_ticks == 5)
                await router.PlaceOrderAsync(new OrderRequest("buy", _c, OrderSide.Buy, OrderType.Market, 1), ct);
            else if (_ticks == 25)
                await router.PlaceOrderAsync(new OrderRequest("sell", _c, OrderSide.Sell, OrderType.Market, 1), ct);
        }
        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    }
}
