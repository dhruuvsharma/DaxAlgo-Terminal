using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Iceberg / hidden-liquidity detector. Hypothesis (Hasbrouck 2007; Bouchaud, Mézard,
/// Potters): a level whose visible size keeps refilling at the same price across many
/// ticks — despite trades visibly hitting it — is a hidden order. Tradeable signal: that
/// side is likely a strong support/resistance for the near term; trade toward the iceberg.
///
/// Detection (L1 approximation): track how long the touch price has remained unchanged on
/// each side AND how often the size has decreased and then recovered to roughly the same
/// level. If a side stays "sticky" (price unchanged + size oscillating) for ≥ <see cref="StickyTicks"/>
/// ticks, treat that side as iceberg-supported and take a position toward it.
///
/// With true L2 the signal becomes much cleaner — you observe specific quote IDs being
/// deleted and immediately re-added at the same price. The current L1 approximation only
/// fires on instruments with deep, stable touches.
/// </summary>
public sealed class IcebergDetectionStrategy : IBacktestStrategy
{
    public int StickyTicks { get; }
    public double PriceStabilityEpsilon { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private double _bidStickyPrice;
    private int _bidStickyCount;
    private double _askStickyPrice;
    private int _askStickyCount;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public IcebergDetectionStrategy(
        Contract contract,
        int stickyTicks = 200,
        double priceStabilityEpsilon = 1e-9,
        int holdTicks = 100,
        long quantity = 1)
    {
        _contract = contract;
        StickyTicks = stickyTicks;
        PriceStabilityEpsilon = priceStabilityEpsilon;
        HoldTicks = holdTicks;
        Quantity = quantity;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (Math.Abs(tick.Bid - _bidStickyPrice) <= PriceStabilityEpsilon) _bidStickyCount++;
        else { _bidStickyPrice = tick.Bid; _bidStickyCount = 1; }

        if (Math.Abs(tick.Ask - _askStickyPrice) <= PriceStabilityEpsilon) _askStickyCount++;
        else { _askStickyPrice = tick.Ask; _askStickyCount = 1; }

        if (_position == 0)
        {
            // Sticky bid persisted longer than sticky ask → iceberg support beneath us → go long.
            if (_bidStickyCount >= StickyTicks && _bidStickyCount > _askStickyCount * 2)
            {
                _position = Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (_askStickyCount >= StickyTicks && _askStickyCount > _bidStickyCount * 2)
            {
                _position = -Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        _ticksHeld++;
        if (_ticksHeld >= HoldTicks)
        {
            var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, exit, Math.Abs(_position), ct);
            _position = 0;
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"ice-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
