using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Time-Weighted Average Price execution. Splits a parent order of <see cref="ParentQuantity"/>
/// into <see cref="Slices"/> equal child market orders, fired evenly across ticks. Mirrors the
/// archetypal broker TWAP algo and lets you measure execution slippage vs. the dataset's TVWAP
/// benchmark.
///
/// Single-direction: pure buy or pure sell, no exits. This is an *execution* strategy, not a
/// directional one — comparing realised vs. naive-fill gives you implementation shortfall.
/// </summary>
public sealed class TwapExecutionStrategy : IBacktestStrategy
{
    public OrderSide Side { get; }
    public long ParentQuantity { get; }
    public int Slices { get; }

    private readonly Contract _contract;
    private int _ticksSeen;
    private int _slicesFired;
    private long _filled;
    private int _orderSeq;
    private long? _spacing;

    public TwapExecutionStrategy(
        Contract contract,
        OrderSide side = OrderSide.Buy,
        long parentQuantity = 100,
        int slices = 10)
    {
        if (slices <= 0) throw new ArgumentOutOfRangeException(nameof(slices));
        if (parentQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(parentQuantity));
        _contract = contract;
        Side = side;
        ParentQuantity = parentQuantity;
        Slices = slices;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        _ticksSeen++;

        // Lock in the inter-slice spacing once we've seen enough ticks to guess the data's
        // density. We use a fixed 200-tick warmup, then a fixed cadence — the parent finishes
        // before the dataset ends as long as the dataset has >> Slices * 200 ticks.
        if (_spacing is null)
        {
            if (_ticksSeen < 50) return;
            _spacing = Math.Max(1, 200);
        }

        if (_slicesFired >= Slices) return;
        if (_ticksSeen % _spacing.Value != 0) return;

        var sliceQty = NextSliceQty();
        if (sliceQty <= 0) return;

        _slicesFired++;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"twap-{++_orderSeq}",
            Contract: _contract,
            Side: Side,
            Type: OrderType.Market,
            Quantity: sliceQty), ct);
    }

    private long NextSliceQty()
    {
        var remaining = ParentQuantity - _filled;
        if (remaining <= 0) return 0;
        var slicesRemaining = Slices - _slicesFired;
        if (slicesRemaining <= 0) return 0;
        var qty = (long)Math.Ceiling((double)remaining / slicesRemaining);
        return Math.Min(qty, remaining);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
    {
        if (evt.LastFillQuantity > 0) _filled += evt.LastFillQuantity;
        return Task.CompletedTask;
    }

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        // Top up any unfilled remainder with a final market order. Real broker TWAPs differ
        // here (some leave the residual to the desk), but for back-testing apples-to-apples,
        // we always achieve the parent quantity.
        var remaining = ParentQuantity - _filled;
        if (remaining <= 0) return;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"twap-tail-{++_orderSeq}",
            Contract: _contract,
            Side: Side,
            Type: OrderType.Market,
            Quantity: remaining), ct);
    }
}
