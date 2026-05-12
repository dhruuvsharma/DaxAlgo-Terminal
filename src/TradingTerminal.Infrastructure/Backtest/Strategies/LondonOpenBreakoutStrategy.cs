using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// London Open Breakout — a famous FX session strategy. Tracks the high/low of the
/// "Asian session" leading into the London open at 08:00 UTC; takes the breakout in the
/// direction that breaks first, with an ATR-multiple trailing stop, flatten at the
/// London close (16:00 UTC).
///
/// Cited in Bob Volman's "Forex Price Action Scalping" (2011) and standard across FX desks
/// as an intraday baseline — works on the cable / fiber pairs (GBPUSD, EURUSD) where the
/// London open delivers a reliable volatility expansion.
///
/// Session boundaries are in UTC: pre-London = 00:00 to <see cref="LondonOpenHourUtc"/>,
/// London = open to <see cref="LondonCloseHourUtc"/>.
/// </summary>
public sealed class LondonOpenBreakoutStrategy : IBacktestStrategy
{
    public int LondonOpenHourUtc { get; }
    public int LondonCloseHourUtc { get; }
    public double AtrStopMultiplier { get; }
    public int AtrPeriod { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.AverageTrueRange _atr;
    private DateTime _currentSessionDate;
    private double _sessionHigh;
    private double _sessionLow;
    private bool _sessionInitialized;
    private long _position;
    private double _stopPrice;
    private int _orderSeq;

    public LondonOpenBreakoutStrategy(
        Contract contract,
        int londonOpenHourUtc = 8,
        int londonCloseHourUtc = 16,
        double atrStopMultiplier = 2.0,
        int atrPeriod = 50,
        long quantity = 1)
    {
        _contract = contract;
        LondonOpenHourUtc = londonOpenHourUtc;
        LondonCloseHourUtc = londonCloseHourUtc;
        AtrStopMultiplier = atrStopMultiplier;
        AtrPeriod = atrPeriod;
        Quantity = quantity;
        _atr = new Indicators.AverageTrueRange(atrPeriod);
        _sessionHigh = double.MinValue;
        _sessionLow = double.MaxValue;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _atr.Push(mid);

        var t = tick.TimestampUtc;
        var sessionDate = t.Date;

        if (sessionDate != _currentSessionDate)
        {
            _currentSessionDate = sessionDate;
            _sessionHigh = double.MinValue;
            _sessionLow = double.MaxValue;
            _sessionInitialized = false;
            // Flatten any inherited inventory at the day rollover.
            if (_position != 0)
            {
                var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, flatSide, Math.Abs(_position), ct);
                _position = 0;
            }
        }

        // Pre-open phase: accumulate Asian session range.
        if (t.Hour < LondonOpenHourUtc)
        {
            if (mid > _sessionHigh) _sessionHigh = mid;
            if (mid < _sessionLow) _sessionLow = mid;
            _sessionInitialized = true;
            return;
        }

        // London close → flatten.
        if (t.Hour >= LondonCloseHourUtc)
        {
            if (_position != 0)
            {
                var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, flatSide, Math.Abs(_position), ct);
                _position = 0;
            }
            return;
        }

        if (!_sessionInitialized || !_atr.IsReady) return;

        if (_position == 0)
        {
            if (tick.Ask > _sessionHigh)
            {
                _position = Quantity;
                _stopPrice = tick.Ask - AtrStopMultiplier * _atr.Value;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (tick.Bid < _sessionLow)
            {
                _position = -Quantity;
                _stopPrice = tick.Bid + AtrStopMultiplier * _atr.Value;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        // ATR trailing stop
        if (_position > 0)
        {
            var trail = mid - AtrStopMultiplier * _atr.Value;
            if (trail > _stopPrice) _stopPrice = trail;
            if (tick.Bid <= _stopPrice) { await Submit(router, OrderSide.Sell, _position, ct); _position = 0; }
        }
        else
        {
            var trail = mid + AtrStopMultiplier * _atr.Value;
            if (trail < _stopPrice) _stopPrice = trail;
            if (tick.Ask >= _stopPrice) { await Submit(router, OrderSide.Buy, -_position, ct); _position = 0; }
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
            ClientOrderId: $"lob-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
