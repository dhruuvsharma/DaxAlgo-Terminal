using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Pure microstructure scalper. Trades the divergence between the size-weighted microprice
/// and the simple mid. Hypothesis (Stoikov / Cartea-Jaimungal style): when the bid stack is
/// much heavier than the ask, the next mid-move is up, and vice versa.
///
/// Entry: when <c>microprice - mid &gt;= EntryThreshold</c> go long; when
///        <c>microprice - mid &lt;= -EntryThreshold</c> go short.
/// Exit: when the signal flips back through zero, or after <see cref="HoldTicks"/> ticks
///       elapse (forced unwind — most microstructure edges decay fast).
///
/// Requires non-zero <c>BidSize</c> / <c>AskSize</c> in the data. NinjaTrader L1 doesn't
/// expose sizes (they're zero) — run this on IB / cTrader / synthetic data only.
/// </summary>
public sealed class MicropriceStrategy : IBacktestStrategy
{
    public double EntryThreshold { get; }
    public int HoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public MicropriceStrategy(
        Contract contract,
        double entryThreshold = 0.001,
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

        var mid = (tick.Bid + tick.Ask) * 0.5;
        var micro = Microstructure.Microprice(tick);
        var signal = micro - mid;

        if (_position == 0)
        {
            if (signal >= EntryThreshold)
            {
                _position = Quantity;
                _ticksHeld = 0;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (signal <= -EntryThreshold)
            {
                _position = -Quantity;
                _ticksHeld = 0;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        _ticksHeld++;
        var signalReversed = _position > 0 ? signal <= 0 : signal >= 0;
        if (signalReversed || _ticksHeld >= HoldTicks)
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

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct)
    {
        var id = $"mp-{++_orderSeq}";
        return router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: id,
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
    }
}
