using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace DaxNewStrategy.Engine;

/// <summary>
/// The strategy's engine kernel - pure signal logic against the backtest contracts, no UI. This
/// demo goes long when a fast EMA of the mid-price crosses above a slow EMA, short on the opposite
/// cross, and flattens at the end of the run. Replace the math; keep the shape:
/// <list type="bullet">
///   <item>own ALL state in fields (one kernel instance per run - no statics),</item>
///   <item>orders only via <see cref="IOrderRouter"/> with idempotent ClientOrderIds,</item>
///   <item>time only via <see cref="IClock"/> (never DateTime.UtcNow - backtests replay the past),</item>
///   <item>no file/network/process access - the host's install-time policy scan flags it.</item>
/// </list>
/// </summary>
public sealed class DaxNewStrategyKernel(
    Contract contract,
    int fastPeriod = 20,
    int slowPeriod = 80,
    long quantity = 1) : IBacktestStrategy
{
    private readonly Contract _contract = contract;
    private readonly int _fastPeriod = Math.Max(2, fastPeriod);
    private readonly int _slowPeriod = Math.Max(3, slowPeriod);
    private readonly long _quantity = Math.Max(1, quantity);
    private double _fastEma;
    private double _slowEma;
    private int _ticksSeen;
    private long _position; // signed units of Quantity: -1 short, 0 flat, +1 long
    private int _orderSeq;  // makes ClientOrderIds unique even at identical timestamps

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) / 2.0;
        if (mid <= 0) return;

        _ticksSeen++;
        if (_ticksSeen == 1)
        {
            _fastEma = _slowEma = mid;
            return;
        }

        _fastEma += 2.0 / (_fastPeriod + 1) * (mid - _fastEma);
        _slowEma += 2.0 / (_slowPeriod + 1) * (mid - _slowEma);
        if (_ticksSeen < _slowPeriod) return; // warm-up

        var want = _fastEma > _slowEma ? 1L : _fastEma < _slowEma ? -1L : _position;
        if (want == _position) return;

        // One order moves straight to the target (a reversal is a single 2xQuantity order).
        var delta = (want - _position) * _quantity;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: NextOrderId(clock),
            Contract: _contract,
            Side: delta > 0 ? OrderSide.Buy : OrderSide.Sell,
            Type: OrderType.Market,
            Quantity: Math.Abs(delta)), ct);
        _position = want;
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;

        // Flatten so the run's P&L is realized, not an open position.
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: NextOrderId(clock),
            Contract: _contract,
            Side: _position > 0 ? OrderSide.Sell : OrderSide.Buy,
            Type: OrderType.Market,
            Quantity: Math.Abs(_position) * _quantity), ct);
        _position = 0;
    }

    private string NextOrderId(IClock clock) =>
        $"dax.new.strategy-{clock.UtcNow:yyyyMMddHHmmssfff}-{_orderSeq++}";
}
