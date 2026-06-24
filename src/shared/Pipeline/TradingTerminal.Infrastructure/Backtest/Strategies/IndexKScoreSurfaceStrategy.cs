using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Single-instrument variant of the Index K-Score Surface strategy. The full multi-stock
/// surface (component weights, per-stock thresholds, cross-sectional piercing aggregation)
/// only makes sense in live mode — see the live-UI strategy project for the multi-stock VM.
/// In backtest mode the engine receives one quote stream; this strategy aggregates ticks
/// into fixed-interval bars, computes the same 15-indicator K-score on each bar close, and
/// enters in the direction of K when |K| exceeds <see cref="EntryThreshold"/>.
///
/// <para>Treat the backtest as a sanity check on the K-score signal in isolation — strong
/// performance on a single instrument is a necessary but not sufficient condition for the
/// full index-aggregation logic to add edge.</para>
/// </summary>
public sealed class IndexKScoreSurfaceStrategy : IBacktestStrategy
{
    public TimeSpan BarInterval { get; }
    public double EntryThreshold { get; }
    public double ExitThreshold { get; }
    public long Quantity { get; }
    public IndexKScoreParameters Parameters { get; }

    private readonly Contract _contract;
    private readonly IndexKScoreCalculator _calc;
    private DateTime _currentBarStart = DateTime.MinValue;
    private double _barOpen, _barHigh, _barLow, _barClose;
    private long _barVolume;
    private long _position;
    private int _orderSeq;

    public IndexKScoreSurfaceStrategy(
        Contract contract,
        TimeSpan? barInterval = null,
        double entryThreshold = 0.40,
        double exitThreshold = 0.10,
        long quantity = 1,
        IndexKScoreParameters? parameters = null)
    {
        if (entryThreshold <= 0 || entryThreshold > 1.5)
            throw new ArgumentOutOfRangeException(nameof(entryThreshold), "Entry threshold must be in (0, 1.5].");
        if (exitThreshold < 0 || exitThreshold >= entryThreshold)
            throw new ArgumentOutOfRangeException(nameof(exitThreshold), "Exit threshold must be in [0, entry).");
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        _contract = contract;
        BarInterval = barInterval ?? TimeSpan.FromMinutes(5);
        EntryThreshold = entryThreshold;
        ExitThreshold = exitThreshold;
        Quantity = quantity;
        Parameters = parameters ?? new IndexKScoreParameters();
        _calc = new IndexKScoreCalculator(Parameters);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        var ts = tick.TimestampUtc;
        var bucket = new DateTime(ts.Ticks - (ts.Ticks % BarInterval.Ticks), DateTimeKind.Utc);

        if (_currentBarStart == DateTime.MinValue)
        {
            _currentBarStart = bucket;
            _barOpen = _barHigh = _barLow = _barClose = mid;
            _barVolume = 1;
            return;
        }

        if (bucket != _currentBarStart)
        {
            var bar = new Bar(_currentBarStart, _barOpen, _barHigh, _barLow, _barClose, _barVolume);
            var snap = _calc.OnBar(bar);
            if (snap is { } s) await EvaluateAsync(s, router, ct).ConfigureAwait(false);

            _currentBarStart = bucket;
            _barOpen = _barHigh = _barLow = _barClose = mid;
            _barVolume = 1;
        }
        else
        {
            if (mid > _barHigh) _barHigh = mid;
            if (mid < _barLow) _barLow = mid;
            _barClose = mid;
            _barVolume++;
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        var qty = Math.Abs(_position);
        _position = 0;
        await SubmitAsync(router, side, qty, ct).ConfigureAwait(false);
    }

    private async Task EvaluateAsync(IndexKScoreCalculator.Snapshot snap, IOrderRouter router, CancellationToken ct)
    {
        var k = snap.KFinal;

        if (_position == 0)
        {
            if (k > EntryThreshold)
            {
                _position = Quantity;
                await SubmitAsync(router, OrderSide.Buy, Quantity, ct).ConfigureAwait(false);
            }
            else if (k < -EntryThreshold)
            {
                _position = -Quantity;
                await SubmitAsync(router, OrderSide.Sell, Quantity, ct).ConfigureAwait(false);
            }
            return;
        }

        // Exit on reversion through zero, sign flip past entry, or |K| below exit floor.
        var sameSide = (_position > 0 && k > 0) || (_position < 0 && k < 0);
        if (!sameSide || Math.Abs(k) < ExitThreshold)
        {
            var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            var qty = Math.Abs(_position);
            _position = 0;
            await SubmitAsync(router, side, qty, ct).ConfigureAwait(false);
        }
    }

    private Task SubmitAsync(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"iks-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
