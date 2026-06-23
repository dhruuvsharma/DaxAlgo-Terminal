using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Mean reversion against an online-estimated Ornstein-Uhlenbeck process.
///
/// Model: <c>dX_t = θ(μ - X_t) dt + σ dW_t</c>. Discretised AR(1):
///   <c>X_{t+1} = a + b * X_t + ε</c>, with <c>b = e^{-θΔt}</c>, <c>a = μ(1-b)</c>,
///   <c>Var(ε) = σ² (1 - b²) / (2θ)</c>.
///
/// Parameters are estimated online via a rolling-window OLS over the last
/// <see cref="Lookback"/> mid prices, refit every <see cref="RefitEvery"/> ticks. We trade
/// the z-score of the current mid relative to the OU stationary distribution:
///   <c>z = (X_t - μ̂) / σ̂_stat</c>, where <c>σ̂_stat² = Var(ε) / (1 - b²) = σ² / (2θ)</c>.
/// Enter long when z ≤ -<see cref="EntryZ"/>; short when z ≥ <see cref="EntryZ"/>; flatten
/// when |z| ≤ <see cref="ExitZ"/>. Stop out at |z| ≥ <see cref="StopZ"/> to bail when the
/// process appears to have broken (regime shift, news).
///
/// Not a real edge on synth data — exists as a more principled cousin of the demo
/// MeanReversionStrategy. Useful as a sanity probe on real intraday data where prices are
/// often locally OU.
/// </summary>
public sealed class OrnsteinUhlenbeckStrategy : IBacktestStrategy
{
    public int Lookback { get; }
    public int RefitEvery { get; }
    public double EntryZ { get; }
    public double ExitZ { get; }
    public double StopZ { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<double> _window;
    private double _muHat;
    private double _sigmaStatHat;
    private int _sinceRefit;
    private long _position;
    private int _orderSeq;

    public OrnsteinUhlenbeckStrategy(
        Contract contract,
        int lookback = 500,
        int refitEvery = 50,
        double entryZ = 2.0,
        double exitZ = 0.25,
        double stopZ = 4.0,
        long quantity = 1)
    {
        _contract = contract;
        Lookback = lookback;
        RefitEvery = refitEvery;
        EntryZ = entryZ;
        ExitZ = exitZ;
        StopZ = stopZ;
        Quantity = quantity;
        _window = new Queue<double>(lookback + 1);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        _window.Enqueue(mid);
        while (_window.Count > Lookback) _window.Dequeue();

        _sinceRefit++;
        if (_sinceRefit >= RefitEvery && _window.Count == Lookback)
        {
            Refit();
            _sinceRefit = 0;
        }

        if (_sigmaStatHat <= 0) return;

        var z = (mid - _muHat) / _sigmaStatHat;

        if (_position == 0)
        {
            if (z <= -EntryZ)
            {
                _position = Quantity;
                await Submit(router, OrderSide.Buy, Quantity, ct);
            }
            else if (z >= EntryZ)
            {
                _position = -Quantity;
                await Submit(router, OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        var exitFlat = Math.Abs(z) <= ExitZ;
        var stopOut = (_position > 0 && z <= -StopZ) || (_position < 0 && z >= StopZ);
        if (exitFlat || stopOut)
        {
            var exitSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, exitSide, Math.Abs(_position), ct);
            _position = 0;
        }
    }

    private void Refit()
    {
        var arr = _window.ToArray();
        var n = arr.Length;

        // OLS: X_{t+1} = a + b * X_t + ε  over (X_0..X_{n-2}) → (X_1..X_{n-1}).
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (var i = 0; i < n - 1; i++)
        {
            var x = arr[i];
            var y = arr[i + 1];
            sumX += x; sumY += y; sumXX += x * x; sumXY += x * y;
        }
        var m = n - 1;
        var denom = m * sumXX - sumX * sumX;
        if (denom == 0) return;
        var b = (m * sumXY - sumX * sumY) / denom;
        var a = (sumY - b * sumX) / m;

        // Residual variance
        double rss = 0;
        for (var i = 0; i < m; i++)
        {
            var fitted = a + b * arr[i];
            var err = arr[i + 1] - fitted;
            rss += err * err;
        }
        var residVar = rss / m;

        if (b is <= 0 or >= 1) return;
        _muHat = a / (1 - b);
        // Stationary variance: residVar / (1 - b²) under the discrete AR(1).
        var statVar = residVar / (1 - b * b);
        _sigmaStatHat = statVar > 0 ? Math.Sqrt(statVar) : 0;
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct)
    {
        var id = $"ou-{++_orderSeq}";
        return router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: id,
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
    }
}
