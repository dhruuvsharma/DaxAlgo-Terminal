using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// First ML-driven strategy in the catalog. An online recursive-least-squares model fits
/// future mid-return on a short feature vector live, with exponential forgetting (λ ≈ 0.99).
/// When the model's prediction exceeds <see cref="EntryThreshold"/> in absolute value, take
/// the predicted direction. The position is closed after <see cref="HoldTicks"/> ticks
/// (matching the model's prediction horizon), which keeps holding-period and label-horizon
/// aligned — a subtle correctness point in supervised intraday systems.
///
/// Features used (all already computed by <see cref="Microstructure"/>):
///   - microprice deviation from mid
///   - queue imbalance
///   - rolling-vol estimate (EWMA on mid returns)
///
/// Label / target: realised log-return over the next <see cref="HoldTicks"/> ticks.
/// We can't see the future at submission, so we update the model with a one-tick-delayed
/// teacher signal (close mid t vs close mid t-HoldTicks), which keeps the fit causal.
///
/// Bias: the model isn't given an intercept term explicitly — features are mean-centred
/// by construction (queue imbalance / microprice deviation oscillate around 0). On
/// instruments where they don't, add an intercept column upstream.
/// </summary>
public sealed class OnlineRegressionAlphaStrategy : IBacktestStrategy
{
    public int HoldTicks { get; }
    public double EntryThreshold { get; }
    public double VolHalfLife { get; }
    public double Lambda { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly OnlineLinearRegression _model;
    private readonly Queue<(double Mid, double[] Features)> _delayBuffer;
    private double _emaVar;
    private double _lastMid;
    private long _position;
    private int _ticksHeld;
    private int _orderSeq;

    public OnlineRegressionAlphaStrategy(
        Contract contract,
        int holdTicks = 50,
        double entryThreshold = 1e-4,
        double volHalfLife = 100,
        double lambda = 0.99,
        long quantity = 1)
    {
        _contract = contract;
        HoldTicks = holdTicks;
        EntryThreshold = entryThreshold;
        VolHalfLife = volHalfLife;
        Lambda = lambda;
        Quantity = quantity;
        _model = new OnlineLinearRegression(dimensions: 3, lambda: lambda);
        _delayBuffer = new Queue<(double, double[])>(holdTicks + 1);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        UpdateEmaVar(mid);
        if (tick.BidSize <= 0 || tick.AskSize <= 0)
        {
            _lastMid = mid;
            return;
        }

        var features = new[]
        {
            Microstructure.Microprice(tick) - mid,
            Microstructure.QueueImbalance(tick),
            Math.Sqrt(_emaVar),
        };

        // Teach the model with a (HoldTicks)-old observation: feature snapshot then,
        // observed forward-return = log(mid_now / mid_then). Keeps the fit causal.
        if (_delayBuffer.Count >= HoldTicks)
        {
            var (oldMid, oldFeats) = _delayBuffer.Dequeue();
            if (oldMid > 0)
            {
                var realised = Math.Log(mid / oldMid);
                _model.Update(oldFeats, realised);
            }
        }
        _delayBuffer.Enqueue((mid, features));

        if (_model.Samples < 200) // warm-up
        {
            _lastMid = mid;
            return;
        }

        var prediction = _model.Predict(features);

        if (_position == 0)
        {
            if (prediction >= EntryThreshold)
            {
                _position = Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (prediction <= -EntryThreshold)
            {
                _position = -Quantity; _ticksHeld = 0;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
        }
        else
        {
            _ticksHeld++;
            if (_ticksHeld >= HoldTicks)
            {
                var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
                await Submit(router, exit, Math.Abs(_position), ct);
                _position = 0;
            }
        }

        _lastMid = mid;
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
    }

    private void UpdateEmaVar(double mid)
    {
        if (_lastMid == 0) { _lastMid = mid; return; }
        var r = (mid - _lastMid) / _lastMid;
        var alpha = 1.0 - Math.Exp(-Math.Log(2.0) / VolHalfLife);
        _emaVar = (1 - alpha) * _emaVar + alpha * r * r;
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"olr-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
