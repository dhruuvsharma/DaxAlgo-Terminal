using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Larry Connors' RSI(2) — short-period RSI deep oversold/overbought reversion.
/// Connors &amp; Alvarez, "Short Term Trading Strategies That Work" (2008), report
/// statistically significant edge across SP500, Forex pairs, and ETFs on the 2-period RSI
/// when buying at RSI ≤ 10 and selling at RSI ≥ 90 (or on close above a 5-period SMA).
///
/// This implementation follows the textbook recipe: buy when RSI(2) ≤ <see cref="EntryRsi"/>,
/// sell-out when RSI(2) ≥ <see cref="ExitRsi"/> OR mid closes above the 5-SMA (configurable
/// via <see cref="ExitSmaPeriod"/>). Symmetric short side. Famous as a baseline; the edge
/// has dampened post-2010 but it remains a standard teaching strategy.
/// </summary>
public sealed class RsiTwoPeriodStrategy : IBacktestStrategy
{
    public int RsiPeriod { get; }
    public double EntryRsi { get; }
    public double ExitRsi { get; }
    public int ExitSmaPeriod { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.RelativeStrengthIndex _rsi;
    private readonly Indicators.SimpleMovingAverage _exitSma;
    private long _position;
    private int _orderSeq;

    public RsiTwoPeriodStrategy(
        Contract contract,
        int rsiPeriod = 2,
        double entryRsi = 10,
        double exitRsi = 90,
        int exitSmaPeriod = 5,
        long quantity = 1)
    {
        _contract = contract;
        RsiPeriod = rsiPeriod;
        EntryRsi = entryRsi;
        ExitRsi = exitRsi;
        ExitSmaPeriod = exitSmaPeriod;
        Quantity = quantity;
        _rsi = new Indicators.RelativeStrengthIndex(rsiPeriod);
        _exitSma = new Indicators.SimpleMovingAverage(exitSmaPeriod);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _rsi.Push(mid);
        _exitSma.Push(mid);
        if (!_rsi.IsReady || !_exitSma.IsReady) return;

        var rsi = _rsi.Value;

        if (_position == 0)
        {
            if (rsi <= EntryRsi) { _position = Quantity; await Submit(router, OrderSide.Buy, Quantity, ct); }
            else if (rsi >= 100 - EntryRsi) { _position = -Quantity; await Submit(router, OrderSide.Sell, Quantity, ct); }
            return;
        }

        if (_position > 0 && (rsi >= ExitRsi || mid > _exitSma.Value))
        {
            await Submit(router, OrderSide.Sell, _position, ct);
            _position = 0;
        }
        else if (_position < 0 && (rsi <= 100 - ExitRsi || mid < _exitSma.Value))
        {
            await Submit(router, OrderSide.Buy, -_position, ct);
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
            ClientOrderId: $"rsi2-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
