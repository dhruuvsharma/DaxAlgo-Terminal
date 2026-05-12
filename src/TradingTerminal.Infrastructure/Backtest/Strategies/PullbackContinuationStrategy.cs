using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Trend pullback continuation. In an uptrend (mid > long SMA), wait for a short-term
/// pullback (price drops more than <see cref="PullbackPct"/> below a near-term high), then
/// enter long on the first tick the trend resumes (mid stops dropping and rises above the
/// pullback low). Symmetric on the short side when in a downtrend.
///
/// The "buy the dip" trade — the workhorse SP500 retail rule (Connors &amp; Sen 2009,
/// "How Markets Really Work") and the structure underneath momentum-overlay rules at AQR /
/// Winton. Combines a long-horizon trend filter with a short-horizon entry trigger to
/// avoid catching falling knives.
/// </summary>
public sealed class PullbackContinuationStrategy : IBacktestStrategy
{
    public int TrendPeriod { get; }
    public int PullbackWindow { get; }
    public double PullbackPct { get; }
    public double StopPct { get; }
    public double TakeProfitPct { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.SimpleMovingAverage _trend;
    private readonly Queue<double> _highWindow;
    private readonly Queue<double> _lowWindow;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public PullbackContinuationStrategy(
        Contract contract,
        int trendPeriod = 200,
        int pullbackWindow = 20,
        double pullbackPct = 0.002,
        double stopPct = 0.005,
        double takeProfitPct = 0.010,
        long quantity = 1)
    {
        _contract = contract;
        TrendPeriod = trendPeriod;
        PullbackWindow = pullbackWindow;
        PullbackPct = pullbackPct;
        StopPct = stopPct;
        TakeProfitPct = takeProfitPct;
        Quantity = quantity;
        _trend = new Indicators.SimpleMovingAverage(trendPeriod);
        _highWindow = new Queue<double>(pullbackWindow);
        _lowWindow = new Queue<double>(pullbackWindow);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _trend.Push(mid);
        _highWindow.Enqueue(mid);
        _lowWindow.Enqueue(mid);
        while (_highWindow.Count > PullbackWindow) _highWindow.Dequeue();
        while (_lowWindow.Count > PullbackWindow) _lowWindow.Dequeue();
        if (!_trend.IsReady || _highWindow.Count < PullbackWindow) return;

        var trend = _trend.Value;
        var recentHigh = _highWindow.Max();
        var recentLow = _lowWindow.Min();

        if (_position == 0)
        {
            // Uptrend pullback: price made a recent high and has since dropped >= PullbackPct.
            if (mid > trend && (recentHigh - mid) / recentHigh >= PullbackPct && tick.Ask >= mid)
            {
                _position = Quantity;
                _entryPrice = tick.Ask;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (mid < trend && (mid - recentLow) / recentLow >= PullbackPct && tick.Bid <= mid)
            {
                _position = -Quantity;
                _entryPrice = tick.Bid;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        if (_position > 0)
        {
            var target = _entryPrice * (1 + TakeProfitPct);
            var stop = _entryPrice * (1 - StopPct);
            if (mid >= target || mid <= stop)
            {
                await Submit(router, OrderSide.Sell, _position, ct);
                _position = 0;
            }
        }
        else
        {
            var target = _entryPrice * (1 - TakeProfitPct);
            var stop = _entryPrice * (1 + StopPct);
            if (mid <= target || mid >= stop)
            {
                await Submit(router, OrderSide.Buy, -_position, ct);
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
            ClientOrderId: $"pb-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
