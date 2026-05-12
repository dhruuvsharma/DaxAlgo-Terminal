using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Market-impact-aware momentum: rides short-term momentum signals only when the order
/// book is deep enough to absorb the entry without breaking through multiple levels.
/// Carver (2015) "Systematic Trading" calls this kind of overlay an "impact cost filter"
/// — it stops trend systems from giving back their edge in execution costs during
/// liquidity droughts (illiquid hours, news vacuum).
///
/// Mechanism: track a rolling mean of the available size on each side. Take a long when
/// mid breaks above the previous N-tick high AND the ask side has at least
/// <see cref="MinDepthRatio"/>× its rolling mean size resting. Symmetric short. Skip the
/// entry entirely when the book is thinner than required — the signal still fires, but
/// the strategy intentionally passes.
///
/// L2 upgrade: replace <c>askSize</c> with <c>SideDepth(snapshot.Asks, topN)</c> from
/// <see cref="Microstructure"/>. The current single-level proxy works on instruments
/// where the touch carries representative depth (e.g. tight FX majors).
/// </summary>
public sealed class ThinBookFilterStrategy : IBacktestStrategy
{
    public int BreakoutLookback { get; }
    public int DepthLookback { get; }
    public double MinDepthRatio { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<double> _highs;
    private readonly Queue<double> _lows;
    private readonly Indicators.SimpleMovingAverage _bidSizeMa;
    private readonly Indicators.SimpleMovingAverage _askSizeMa;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public ThinBookFilterStrategy(
        Contract contract,
        int breakoutLookback = 100,
        int depthLookback = 200,
        double minDepthRatio = 1.0,
        int holdTicks = 200,
        long quantity = 1)
    {
        _contract = contract;
        BreakoutLookback = breakoutLookback;
        DepthLookback = depthLookback;
        MinDepthRatio = minDepthRatio;
        HoldTicks = holdTicks;
        Quantity = quantity;
        _highs = new Queue<double>(breakoutLookback);
        _lows = new Queue<double>(breakoutLookback);
        _bidSizeMa = new Indicators.SimpleMovingAverage(depthLookback);
        _askSizeMa = new Indicators.SimpleMovingAverage(depthLookback);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;

        _bidSizeMa.Push(tick.BidSize);
        _askSizeMa.Push(tick.AskSize);

        if (_position == 0 && _highs.Count == BreakoutLookback && _bidSizeMa.IsReady)
        {
            double channelHigh = double.MinValue, channelLow = double.MaxValue;
            foreach (var h in _highs) if (h > channelHigh) channelHigh = h;
            foreach (var l in _lows) if (l < channelLow) channelLow = l;

            var askDeepEnough = tick.AskSize >= _askSizeMa.Value * MinDepthRatio;
            var bidDeepEnough = tick.BidSize >= _bidSizeMa.Value * MinDepthRatio;

            if (tick.Ask > channelHigh && askDeepEnough)
            {
                _position = Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (tick.Bid < channelLow && bidDeepEnough)
            {
                _position = -Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
        }
        else if (_position != 0)
        {
            _ticksHeld++;
            if (_ticksHeld >= HoldTicks)
            {
                var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, exit, Math.Abs(_position), ct);
                _position = 0;
            }
        }

        _highs.Enqueue(tick.Bid);
        _lows.Enqueue(tick.Ask);
        while (_highs.Count > BreakoutLookback) _highs.Dequeue();
        while (_lows.Count > BreakoutLookback) _lows.Dequeue();
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
            ClientOrderId: $"thin-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
