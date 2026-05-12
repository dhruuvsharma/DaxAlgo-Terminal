using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Volatility-targeted long-bias on a single index. Position size = min(<see cref="MaxQuantity"/>,
/// round(<see cref="TargetVol"/> / realised_vol_estimate)). Realised vol is an EWMA of squared
/// returns. The canonical "vol targeting" overlay underpinning AQR's risk-parity products and
/// most managed-futures funds (Asness, Moskowitz, Pedersen 2013, "Value and Momentum Everywhere").
///
/// When vol spikes (e.g. Aug 2015, Feb 2018, Mar 2020 on SPY), the strategy shrinks exposure;
/// when vol compresses, it scales up. On SP500 cash this stabilises max-drawdown materially.
/// Long-only here; flip a sign in code to make it symmetric.
/// </summary>
public sealed class VolatilityTargetedStrategy : IBacktestStrategy
{
    public double TargetVol { get; }
    public double VolHalfLife { get; }
    public long MaxQuantity { get; }
    public int RebalanceEveryTicks { get; }

    private readonly Contract _contract;
    private double _emaVar;
    private double _lastMid;
    private long _position;
    private int _ticksSinceRebalance;
    private int _ticksSeen;
    private int _orderSeq;

    public VolatilityTargetedStrategy(
        Contract contract,
        double targetVol = 0.001,
        double volHalfLife = 200.0,
        long maxQuantity = 10,
        int rebalanceEveryTicks = 100)
    {
        _contract = contract;
        TargetVol = targetVol;
        VolHalfLife = volHalfLife;
        MaxQuantity = maxQuantity;
        RebalanceEveryTicks = rebalanceEveryTicks;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        UpdateVariance(mid);
        _ticksSeen++;
        _ticksSinceRebalance++;
        if (_ticksSeen < 30 || _ticksSinceRebalance < RebalanceEveryTicks) return;
        _ticksSinceRebalance = 0;

        var sigma = Math.Sqrt(Math.Max(_emaVar, 1e-12));
        var raw = TargetVol / sigma;
        var desired = (long)Math.Clamp(Math.Round(raw), 1, MaxQuantity);
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

    private void UpdateVariance(double mid)
    {
        if (_lastMid == 0) { _lastMid = mid; return; }
        var ret = (mid - _lastMid) / _lastMid;
        var alpha = 1.0 - Math.Exp(-Math.Log(2.0) / VolHalfLife);
        _emaVar = (1 - alpha) * _emaVar + alpha * ret * ret;
        _lastMid = mid;
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"voltarg-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
