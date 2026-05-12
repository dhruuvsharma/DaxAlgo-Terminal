using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Avellaneda-Stoikov optimal market maker (2008). Posts symmetric limit quotes around an
/// inventory-shifted reservation price; the spread widens as the time-to-horizon shrinks
/// or volatility rises.
///
/// Formulae (using the simplified closed-form solution):
///   reservation r(t) = S(t) - q·γ·σ²·(T - t)
///   half-spread δ    = (γ·σ²·(T - t) + (2/γ)·ln(1 + γ/k)) / 2
/// where
///   S(t)  current mid
///   q     signed inventory
///   γ     risk aversion (we use <see cref="Gamma"/>)
///   σ²    online variance of mid returns
///   T-t   remaining horizon, normalised to [0, 1] over the backtest's tick budget
///   k     order arrival intensity proxy (we use <see cref="K"/>)
///
/// Each tick: cancel previous quotes, recompute, repost. Limit orders flow through the
/// existing <c>L1FillModel</c>, which fills passive quotes when the touch reaches them.
///
/// CAVEATS (be honest with this in a quant interview):
///  - The fill model is queue-optimistic: it fills your full quantity the moment the touch
///    crosses your price. Real market-making backtests need a queue-position model.
///  - There's no adverse-selection term — toxic flow is invisible to the model.
///  - Variance is a naive EWMA on mid returns, not a microstructure-noise-aware estimator.
/// </summary>
public sealed class AvellanedaStoikovStrategy : IBacktestStrategy
{
    public double Gamma { get; }
    public double K { get; }
    public double VarianceHalfLife { get; }
    public long QuoteSize { get; }
    public long MaxInventory { get; }
    public int HorizonTicks { get; }
    public int RequoteEveryTicks { get; }

    private readonly Contract _contract;
    private double _emaVar;
    private double _lastMid;
    private long _position;
    private int _ticksSeen;
    private int _ticksSinceQuote;
    private int _orderSeq;
    private string? _bidId;
    private string? _askId;

    public AvellanedaStoikovStrategy(
        Contract contract,
        double gamma = 0.1,
        double k = 1.5,
        double varianceHalfLife = 200.0,
        long quoteSize = 1,
        long maxInventory = 5,
        int horizonTicks = 5_000,
        int requoteEveryTicks = 100)
    {
        _contract = contract;
        Gamma = gamma;
        K = k;
        VarianceHalfLife = varianceHalfLife;
        QuoteSize = quoteSize;
        MaxInventory = maxInventory;
        HorizonTicks = horizonTicks;
        RequoteEveryTicks = requoteEveryTicks;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        UpdateVariance(mid);
        _ticksSeen++;
        _ticksSinceQuote++;

        // Real market-makers don't repost every tick — orders need time to rest and earn the
        // spread. Requote only every RequoteEveryTicks, OR when a fill has emptied a side.
        var bidFilled = _bidId is null;
        var askFilled = _askId is null;
        var needRequote = _ticksSinceQuote >= RequoteEveryTicks || bidFilled || askFilled;
        if (!needRequote) return;
        _ticksSinceQuote = 0;

        // Cancel previous round of quotes; we'll repost.
        if (_bidId is { } b) { await router.CancelOrderAsync(b, ct); _bidId = null; }
        if (_askId is { } a) { await router.CancelOrderAsync(a, ct); _askId = null; }

        if (_ticksSeen < 30) return;  // need some variance estimate

        var timeRemaining = Math.Max(0.0, 1.0 - (double)_ticksSeen / HorizonTicks);
        if (timeRemaining <= 0) return;

        var sigma2 = Math.Max(_emaVar, 1e-12);
        var reservation = mid - _position * Gamma * sigma2 * timeRemaining;
        var halfSpread = 0.5 * (Gamma * sigma2 * timeRemaining + (2.0 / Gamma) * Math.Log(1.0 + Gamma / K));

        var bidPrice = reservation - halfSpread;
        var askPrice = reservation + halfSpread;

        // Don't post a quote that would push us beyond the inventory cap.
        var canBuyMore = _position + QuoteSize <= MaxInventory;
        var canSellMore = _position - QuoteSize >= -MaxInventory;

        if (canBuyMore)
        {
            _bidId = $"as-b-{++_orderSeq}";
            await router.PlaceOrderAsync(new OrderRequest(
                ClientOrderId: _bidId,
                Contract: _contract,
                Side: OrderSide.Buy,
                Type: OrderType.Limit,
                Quantity: QuoteSize,
                LimitPrice: bidPrice), ct);
        }
        if (canSellMore)
        {
            _askId = $"as-a-{++_orderSeq}";
            await router.PlaceOrderAsync(new OrderRequest(
                ClientOrderId: _askId,
                Contract: _contract,
                Side: OrderSide.Sell,
                Type: OrderType.Limit,
                Quantity: QuoteSize,
                LimitPrice: askPrice), ct);
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
    {
        if (evt.LastFillQuantity > 0)
        {
            var signed = evt.Side == OrderSide.Buy ? evt.LastFillQuantity : -evt.LastFillQuantity;
            _position += signed;
        }
        // Clear the quote id on any terminal state so the next tick repost-loop knows the
        // side is free and can post fresh.
        if (evt.State is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected)
        {
            if (evt.ClientOrderId == _bidId) _bidId = null;
            if (evt.ClientOrderId == _askId) _askId = null;
        }
        return Task.CompletedTask;
    }

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_bidId is { } b) await router.CancelOrderAsync(b, ct);
        if (_askId is { } a) await router.CancelOrderAsync(a, ct);
        if (_position == 0) return;
        var flatSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"as-flat-{++_orderSeq}",
            Contract: _contract,
            Side: flatSide,
            Type: OrderType.Market,
            Quantity: Math.Abs(_position)), ct);
    }

    private void UpdateVariance(double mid)
    {
        if (_lastMid == 0) { _lastMid = mid; return; }
        var ret = (mid - _lastMid) / _lastMid;
        var alpha = 1.0 - Math.Exp(-Math.Log(2.0) / VarianceHalfLife);
        _emaVar = (1.0 - alpha) * _emaVar + alpha * ret * ret;
        _lastMid = mid;
    }
}
