using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Overnight gap-fade for index futures / ETFs. Detects "session gap" as an inter-tick
/// time delta ≥ <see cref="OvernightGapMinutes"/> combined with a price jump ≥
/// <see cref="MinGapPct"/>. Fades the gap (sell into a gap-up, buy a gap-down) targeting a
/// return to the previous-session close. Hard stop at <see cref="StopGapMultiples"/>×
/// the gap size.
///
/// Studied extensively on SPY / ES: Bouchaud &amp; Potters' "Theory of Financial Risk and
/// Derivative Pricing" notes a statistically significant tendency for overnight gaps on
/// equity indices to partially fade within the first 30 minutes of the cash session — the
/// classic "opening gap fade" trade.
/// </summary>
public sealed class GapFadeStrategy : IBacktestStrategy
{
    public double OvernightGapMinutes { get; }
    public double MinGapPct { get; }
    public double StopGapMultiples { get; }
    public int MaxHoldTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private DateTime? _lastTickTime;
    private double _lastSessionClose;
    private double _gapEntryPrice;
    private double _gapSize;
    private long _position;
    private int _ticksInPosition;
    private int _orderSeq;

    public GapFadeStrategy(
        Contract contract,
        double overnightGapMinutes = 60,
        double minGapPct = 0.002,
        double stopGapMultiples = 1.5,
        int maxHoldTicks = 1000,
        long quantity = 1)
    {
        _contract = contract;
        OvernightGapMinutes = overnightGapMinutes;
        MinGapPct = minGapPct;
        StopGapMultiples = stopGapMultiples;
        MaxHoldTicks = maxHoldTicks;
        Quantity = quantity;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;

        if (_lastTickTime is { } prev && _position == 0 && _lastSessionClose > 0)
        {
            var gapMinutes = (tick.TimestampUtc - prev).TotalMinutes;
            if (gapMinutes >= OvernightGapMinutes)
            {
                var gapPct = (mid - _lastSessionClose) / _lastSessionClose;
                if (Math.Abs(gapPct) >= MinGapPct)
                {
                    _gapEntryPrice = mid;
                    _gapSize = Math.Abs(mid - _lastSessionClose);
                    _ticksInPosition = 0;
                    if (gapPct > 0)
                    {
                        _position = -Quantity;
                        await Submit(router, OrderSide.Sell, Quantity, ct);
                    }
                    else
                    {
                        _position = Quantity;
                        await Submit(router, OrderSide.Buy, Quantity, ct);
                    }
                }
            }
        }

        _lastSessionClose = mid;
        _lastTickTime = tick.TimestampUtc;

        if (_position == 0) return;

        _ticksInPosition++;
        var target = _gapEntryPrice - Math.Sign(_position) * _gapSize; // back to prior close
        var stop = _gapEntryPrice + Math.Sign(_position) * (_gapSize * StopGapMultiples);
        var hitTarget = _position > 0 ? mid >= target : mid <= target;
        var hitStop = _position > 0 ? mid <= stop : mid >= stop;
        var timeout = _ticksInPosition >= MaxHoldTicks;

        if (hitTarget || hitStop || timeout)
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
            ClientOrderId: $"gap-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
