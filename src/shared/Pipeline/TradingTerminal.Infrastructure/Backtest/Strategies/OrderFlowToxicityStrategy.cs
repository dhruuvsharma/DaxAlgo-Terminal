using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// VPIN-style order-flow toxicity. Easley, López de Prado &amp; O'Hara (2012)
/// "Flow Toxicity and Liquidity in a High-Frequency World" proposed the
/// Volume-Synchronised Probability of Informed Trading: bucket trades by volume, classify
/// each bucket as buy- or sell-initiated via the tick rule, and call the running |buy − sell|
/// / total ratio the toxicity. High toxicity → informed flow is in the market → market
/// makers widen quotes and the next move tends to be against the prevailing aggressor.
///
/// L1 tick-rule approximation: when a tick's mid is up from the previous, classify the
/// "volume" (proxied by aggregate L1 size) as buy-initiated; down ⇒ sell-initiated.
/// Toxicity = rolling |buy − sell| / total across <see cref="WindowTicks"/> ticks. Enter
/// counter to the prevailing imbalance when toxicity exceeds <see cref="ToxicityThreshold"/>
/// (mean-reversion bet that the market overshoots on toxic flow).
///
/// True implementation with L2 would use trade-by-trade classification + actual filled
/// volumes; this is the textbook L1 fallback.
/// </summary>
public sealed class OrderFlowToxicityStrategy : IBacktestStrategy
{
    public int WindowTicks { get; }
    public double ToxicityThreshold { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<double> _signedFlow;
    private double _flowSum;
    private double _absFlowSum;
    private double _prevMid;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public OrderFlowToxicityStrategy(
        Contract contract,
        int windowTicks = 200,
        double toxicityThreshold = 0.55,
        int holdTicks = 100,
        long quantity = 1)
    {
        _contract = contract;
        WindowTicks = windowTicks;
        ToxicityThreshold = toxicityThreshold;
        HoldTicks = holdTicks;
        Quantity = quantity;
        _signedFlow = new Queue<double>(windowTicks);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        if (_prevMid == 0) { _prevMid = mid; return; }

        var volProxy = tick.BidSize + tick.AskSize;
        if (volProxy <= 0) volProxy = 1;
        var direction = mid > _prevMid ? +1 : (mid < _prevMid ? -1 : 0);
        var signed = direction * volProxy;

        _signedFlow.Enqueue(signed);
        _flowSum += signed;
        _absFlowSum += Math.Abs(signed);
        while (_signedFlow.Count > WindowTicks)
        {
            var old = _signedFlow.Dequeue();
            _flowSum -= old;
            _absFlowSum -= Math.Abs(old);
        }
        _prevMid = mid;

        if (_signedFlow.Count < WindowTicks || _absFlowSum <= 0) return;

        var toxicity = Math.Abs(_flowSum) / _absFlowSum;
        if (_position == 0 && toxicity >= ToxicityThreshold)
        {
            // Aggressor side was buyers (flow > 0) → expect mean-reversion → short. Symmetric.
            if (_flowSum > 0) { _position = -Quantity; _ticksHeld = 0; await Submit(router, OrderSide.Sell, Quantity, ct); }
            else { _position = Quantity; _ticksHeld = 0; await Submit(router, OrderSide.Buy, Quantity, ct); }
            return;
        }

        if (_position != 0)
        {
            _ticksHeld++;
            if (_ticksHeld >= HoldTicks)
            {
                var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, exit, Math.Abs(_position), ct);
                _position = 0;
            }
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
            ClientOrderId: $"vpin-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
