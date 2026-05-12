using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Rolling-window z-score anomaly detector. Maintains running mean/stdev of three
/// microstructure features (spread, |queue imbalance|, |1-tick return|); when any
/// feature's z-score crosses <see cref="ZScoreThreshold"/>, fires a signal. Useful as
/// a risk filter (skip entries during regime breaks), as an exchange-glitch detector,
/// and as a flash-crash precursor monitor.
///
/// For live use via the signal-host, every anomaly fires a Buy AND a Sell signal in
/// quick succession (so both Telegram/Discord transports surface the alert). The
/// downstream execution app then ignores the directional side and just treats the
/// notification as an "anomaly observed at time X" event. Crude but it works without
/// adding new <c>NotificationKind</c> values.
///
/// True isolation-forest-style anomaly detection would be more sophisticated; for a
/// dependency-free first pass the rolling z-score covers the same failure modes
/// (sudden spread blowout, queue-imbalance singularity, vol spike).
/// </summary>
public sealed class AnomalyDetectorStrategy : IBacktestStrategy
{
    public int Window { get; }
    public double ZScoreThreshold { get; }
    public int CooldownTicks { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Indicators.RollingStdev _spreadStat;
    private readonly Indicators.RollingStdev _qiStat;
    private readonly Indicators.RollingStdev _retStat;
    private double _lastMid;
    private int _ticksUntilNextAlert;
    private int _orderSeq;

    public AnomalyDetectorStrategy(
        Contract contract,
        int window = 200,
        double zScoreThreshold = 4.0,
        int cooldownTicks = 100,
        long quantity = 1)
    {
        _contract = contract;
        Window = window;
        ZScoreThreshold = zScoreThreshold;
        CooldownTicks = cooldownTicks;
        Quantity = quantity;
        _spreadStat = new Indicators.RollingStdev(window);
        _qiStat = new Indicators.RollingStdev(window);
        _retStat = new Indicators.RollingStdev(window);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        if (_lastMid <= 0) { _lastMid = mid; return; }

        var ret = Math.Abs(mid - _lastMid) / _lastMid;
        var spread = tick.Ask - tick.Bid;
        var qi = Math.Abs(Microstructure.QueueImbalance(tick));
        _spreadStat.Push(spread);
        _qiStat.Push(qi);
        _retStat.Push(ret);
        _lastMid = mid;

        if (_ticksUntilNextAlert > 0) { _ticksUntilNextAlert--; return; }
        if (!_spreadStat.IsReady) return;

        var anomaly = Z(spread, _spreadStat) > ZScoreThreshold
                   || Z(qi, _qiStat) > ZScoreThreshold
                   || Z(ret, _retStat) > ZScoreThreshold;
        if (!anomaly) return;

        // Fire a buy + sell signal pair so the notifier surfaces both sides; in signal
        // mode this becomes one "anomaly observed" pair on the configured transports.
        await Submit(router, OrderSide.Buy, Quantity, ct);
        await Submit(router, OrderSide.Sell, Quantity, ct);
        _ticksUntilNextAlert = CooldownTicks;
    }

    private static double Z(double value, Indicators.RollingStdev stat)
    {
        var s = stat.Value;
        return s <= 0 ? 0 : Math.Abs((value - stat.Mean) / s);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"anom-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
