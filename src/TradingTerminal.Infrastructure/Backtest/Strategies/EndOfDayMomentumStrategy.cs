using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// End-of-day intraday momentum for index futures / SP500 ETFs. Tracks the day's
/// open-to-now return; in the final <see cref="LastFractionOfDay"/> of the UTC trading
/// day, take a position in the direction of the day's existing trend, hold to close.
///
/// Heston, Korajczyk &amp; Sadka (2010) "Intraday Patterns in the Cross-Section of Stock
/// Returns" and Gao, Han, Li &amp; Zhou (2018) "Market Intraday Momentum" both document
/// significant end-of-day momentum on SPY: the last half-hour return correlates
/// positively with the first half-hour return. This strategy implements the simplest
/// member of that family — long if the day so far is up, short if down, but only after
/// the trigger time.
/// </summary>
public sealed class EndOfDayMomentumStrategy : IBacktestStrategy
{
    public double LastFractionOfDay { get; }
    public double MinDayReturn { get; }
    public int SessionStartHourUtc { get; }
    public int SessionEndHourUtc { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private DateTime _currentDayUtc;
    private double _dayOpen;
    private long _position;
    private int _orderSeq;

    public EndOfDayMomentumStrategy(
        Contract contract,
        double lastFractionOfDay = 0.10,
        double minDayReturn = 0.0005,
        int sessionStartHourUtc = 13,  // ~ 9:30am ET in summer
        int sessionEndHourUtc = 20,    // ~ 4:00pm ET in summer
        long quantity = 1)
    {
        _contract = contract;
        LastFractionOfDay = lastFractionOfDay;
        MinDayReturn = minDayReturn;
        SessionStartHourUtc = sessionStartHourUtc;
        SessionEndHourUtc = sessionEndHourUtc;
        Quantity = quantity;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        var t = tick.TimestampUtc;
        var day = t.Date;

        if (day != _currentDayUtc)
        {
            // Day rollover — flatten and reset.
            if (_position != 0)
            {
                var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, flatSide, Math.Abs(_position), ct);
                _position = 0;
            }
            _currentDayUtc = day;
            _dayOpen = 0;
        }

        // Out-of-session — ignore.
        if (t.Hour < SessionStartHourUtc || t.Hour >= SessionEndHourUtc) return;

        // First in-session tick of the day → record the open.
        if (_dayOpen == 0) _dayOpen = mid;

        var sessionLengthHours = SessionEndHourUtc - SessionStartHourUtc;
        var hoursIntoSession = t.Hour + t.Minute / 60.0 - SessionStartHourUtc;
        var fractionComplete = hoursIntoSession / sessionLengthHours;
        if (fractionComplete < 1.0 - LastFractionOfDay) return;

        if (_position == 0)
        {
            var dayReturn = (mid - _dayOpen) / _dayOpen;
            if (dayReturn >= MinDayReturn)
            {
                _position = Quantity;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (dayReturn <= -MinDayReturn)
            {
                _position = -Quantity;
                await Submit(router, OrderSide.Sell, Quantity, ct);
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
            ClientOrderId: $"eod-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
