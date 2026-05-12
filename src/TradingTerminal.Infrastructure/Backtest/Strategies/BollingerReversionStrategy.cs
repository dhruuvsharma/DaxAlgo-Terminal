using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Bollinger Band reversion — long when mid drops below <c>lower = SMA(N) - k·σ(N)</c>,
/// short when it rises above <c>upper = SMA(N) + k·σ(N)</c>. Exit when price reverts to
/// the SMA. Hard stop at <see cref="StopBandMultiplier"/>·σ beyond entry to bail when the
/// band breaks (regime change).
///
/// Canonical mean-reversion baseline; works best in range-bound FX pairs (EURGBP, USDCHF)
/// and breaks down in strong trends. Documented in Bollinger's "Bollinger on Bollinger
/// Bands" (2001).
/// </summary>
public sealed class BollingerReversionStrategy : IBacktestStrategy
{
    public int Period { get; }
    public double EntryStd { get; }
    public double StopBandMultiplier { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.RollingStdev _stat;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public BollingerReversionStrategy(
        Contract contract,
        int period = 20,
        double entryStd = 2.0,
        double stopBandMultiplier = 3.0,
        long quantity = 1)
    {
        _contract = contract;
        Period = period;
        EntryStd = entryStd;
        StopBandMultiplier = stopBandMultiplier;
        Quantity = quantity;
        _stat = new Indicators.RollingStdev(period);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _stat.Push(mid);
        if (!_stat.IsReady) return;

        var mean = _stat.Mean;
        var sd = _stat.Value;
        if (sd <= 0) return;

        var upper = mean + EntryStd * sd;
        var lower = mean - EntryStd * sd;
        var upperStop = mean + StopBandMultiplier * sd;
        var lowerStop = mean - StopBandMultiplier * sd;

        if (_position == 0)
        {
            if (mid <= lower) { _position = Quantity; _entryPrice = tick.Ask; await Submit(router, OrderSide.Buy, Quantity, ct); }
            else if (mid >= upper) { _position = -Quantity; _entryPrice = tick.Bid; await Submit(router, OrderSide.Sell, Quantity, ct); }
            return;
        }

        if (_position > 0)
        {
            var hitTarget = mid >= mean;
            var hitStop = mid <= lowerStop;
            if (hitTarget || hitStop) { await Submit(router, OrderSide.Sell, _position, ct); _position = 0; }
        }
        else
        {
            var hitTarget = mid <= mean;
            var hitStop = mid >= upperStop;
            if (hitTarget || hitStop) { await Submit(router, OrderSide.Buy, -_position, ct); _position = 0; }
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
            ClientOrderId: $"bb-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
