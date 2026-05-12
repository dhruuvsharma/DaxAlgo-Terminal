using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Aggressive-flow / liquidity-sweep detector. Tracks a rolling mean of the touch sizes
/// on each side. When the bid (or ask) size drops sharply relative to its rolling mean
/// — and the touch price also moves in the opposite direction — interpret as aggressive
/// flow hitting that side: an absorption-then-flush pattern. Trade in the direction of
/// the sweep (momentum).
///
/// With true L2 (when wired): the rolling mean would be over <c>SideDepth</c> aggregated
/// across the top N levels, dramatically reducing false positives from one-level reloads.
/// Today the strategy compares only touch sizes; the structural idea is the same.
/// </summary>
public sealed class LiquiditySweepStrategy : IBacktestStrategy
{
    public int Lookback { get; }
    public double SweepRatio { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.SimpleMovingAverage _bidSizeMa;
    private readonly Indicators.SimpleMovingAverage _askSizeMa;
    private double _prevBid;
    private double _prevAsk;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public LiquiditySweepStrategy(
        Contract contract,
        int lookback = 100,
        double sweepRatio = 0.40,
        int holdTicks = 50,
        long quantity = 1)
    {
        _contract = contract;
        Lookback = lookback;
        SweepRatio = sweepRatio;
        HoldTicks = holdTicks;
        Quantity = quantity;
        _bidSizeMa = new Indicators.SimpleMovingAverage(lookback);
        _askSizeMa = new Indicators.SimpleMovingAverage(lookback);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (tick.BidSize <= 0 || tick.AskSize <= 0)
        {
            _prevBid = tick.Bid; _prevAsk = tick.Ask;
            return;
        }

        _bidSizeMa.Push(tick.BidSize);
        _askSizeMa.Push(tick.AskSize);

        if (!_bidSizeMa.IsReady || !_askSizeMa.IsReady)
        {
            _prevBid = tick.Bid; _prevAsk = tick.Ask;
            return;
        }

        var bidMean = _bidSizeMa.Value;
        var askMean = _askSizeMa.Value;

        // Bid-side sweep: bid size collapses AND bid price falls → sellers ran through the bid
        // Ask-side sweep: ask size collapses AND ask price rises → buyers ran through the ask
        var bidSwept = tick.BidSize < bidMean * SweepRatio && tick.Bid < _prevBid;
        var askSwept = tick.AskSize < askMean * SweepRatio && tick.Ask > _prevAsk;

        if (_position == 0)
        {
            if (bidSwept) { _position = -Quantity; _ticksHeld = 0; await Submit(router, OrderSide.Sell, Quantity, ct); }
            else if (askSwept) { _position = Quantity; _ticksHeld = 0; await Submit(router, OrderSide.Buy, Quantity, ct); }
        }
        else
        {
            _ticksHeld++;
            if (_ticksHeld >= HoldTicks)
            {
                var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, exit, Math.Abs(_position), ct);
                _position = 0;
            }
        }

        _prevBid = tick.Bid;
        _prevAsk = tick.Ask;
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
            ClientOrderId: $"sweep-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
