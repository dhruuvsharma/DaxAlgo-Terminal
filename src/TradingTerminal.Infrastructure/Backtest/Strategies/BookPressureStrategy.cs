using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Cumulative order-book imbalance signal. With true L2 data this is exactly the
/// <see cref="Microstructure.CumulativeImbalance"/> across the top N levels — used by
/// every HFT desk to gauge near-term price pressure (Cartea, Jaimungal, Penalva 2015,
/// "Algorithmic and High-Frequency Trading"). With L1 only (the current backtest data
/// path), we compute the imbalance from the touch sizes <c>BidSize</c>/<c>AskSize</c> —
/// a degenerate 1-level imbalance.
///
/// When the parquet tick reader learns to carry <see cref="DepthSnapshot"/>, swap the
/// <c>QueueImbalance(tick.BidSize, tick.AskSize)</c> call below for
/// <c>CumulativeImbalance(tick.Depth!, depthLevels: 5)</c> — no other change.
/// </summary>
public sealed class BookPressureStrategy : IBacktestStrategy
{
    public double EntryThreshold { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public BookPressureStrategy(
        Contract contract,
        double entryThreshold = 0.35,
        int holdTicks = 50,
        long quantity = 1)
    {
        _contract = contract;
        EntryThreshold = entryThreshold;
        HoldTicks = holdTicks;
        Quantity = quantity;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (tick.BidSize <= 0 || tick.AskSize <= 0) return;

        var imbalance = Microstructure.QueueImbalance(tick.BidSize, tick.AskSize);

        if (_position == 0)
        {
            if (imbalance >= EntryThreshold)
            {
                _position = Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (imbalance <= -EntryThreshold)
            {
                _position = -Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        _ticksHeld++;
        var reversed = (_position > 0 && imbalance < 0) || (_position < 0 && imbalance > 0);
        if (reversed || _ticksHeld >= HoldTicks)
        {
            var exitSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, exitSide, Math.Abs(_position), ct);
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
            ClientOrderId: $"bp-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
