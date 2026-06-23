using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Demo <see cref="IBacktestStrategy"/>: market-buy on the first tick, market-sell on the
/// last. Useful as a smoke test for the engine / CLI / data pipeline — the resulting
/// trade should equal the dataset's range PnL minus slippage.
/// </summary>
public sealed class BuyAndHoldStrategy : IBacktestStrategy
{
    private readonly Contract _contract;
    private readonly long _quantity;
    private bool _entered;
    private Tick? _lastTick;

    public BuyAndHoldStrategy(Contract contract, long quantity = 1)
    {
        _contract = contract;
        _quantity = quantity;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        _lastTick = tick;
        if (_entered) return;
        _entered = true;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: "bh-entry",
            Contract: _contract,
            Side: OrderSide.Buy,
            Type: OrderType.Market,
            Quantity: _quantity), ct);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_lastTick is null) return;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: "bh-exit",
            Contract: _contract,
            Side: OrderSide.Sell,
            Type: OrderType.Market,
            Quantity: _quantity), ct);
    }
}
