using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Long when price is above the long-period SMA, flat otherwise. The 200-day-SMA filter
/// is the most studied passive-overlay trend filter on the S&amp;P 500 — Mebane Faber's
/// "A Quantitative Approach to Tactical Asset Allocation" (2007) shows it reduces drawdown
/// dramatically without sacrificing long-run return when applied to SPY.
///
/// Long-only by default (set <see cref="AllowShort"/> = true to short when below). On
/// tick data, "200-day" is a misnomer; the period is in ticks. For real daily-bar
/// behavior, run on a parquet file that contains daily-frequency ticks (one per day's close).
/// </summary>
public sealed class TrendFilterStrategy : IBacktestStrategy
{
    public int Period { get; }
    public bool AllowShort { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.SimpleMovingAverage _sma;
    private long _position;
    private int _orderSeq;

    public TrendFilterStrategy(
        Contract contract,
        int period = 200,
        bool allowShort = false,
        long quantity = 1)
    {
        _contract = contract;
        Period = period;
        AllowShort = allowShort;
        Quantity = quantity;
        _sma = new Indicators.SimpleMovingAverage(period);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _sma.Push(mid);
        if (!_sma.IsReady) return;

        var desired = mid > _sma.Value ? Quantity : (AllowShort ? -Quantity : 0);
        if (desired == _position) return;

        var delta = desired - _position;
        var side = delta > 0 ? OrderSide.Buy : OrderSide.Sell;
        await Submit(router, side, Math.Abs(delta), ct);
        _position = desired;
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
            ClientOrderId: $"trend-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
