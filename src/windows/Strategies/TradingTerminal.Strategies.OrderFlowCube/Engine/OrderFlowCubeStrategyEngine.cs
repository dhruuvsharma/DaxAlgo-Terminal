using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Strategies.OrderFlowCube.Engine;

/// <summary>
/// "Order Flow Cube" regime strategy — the first in the multi-variable regime-cube series
/// (see <c>ideas.md</c>). Consumes the trade tape (<see cref="IBacktestStrategy.OnTradeAsync"/>)
/// and maintains three signals:
/// <list type="bullet">
/// <item><b>CVD imbalance</b> ∈ [-1, +1]: signed-flow / total-flow over the rolling window.
/// Positive ⇒ buyers in control over the window.</item>
/// <item><b>Aggressor ratio</b> ∈ [0, 1]: buy-aggressor volume / total volume. 0.5 = balanced.</item>
/// <item><b>Size ratio</b>: window mean trade size / baseline mean trade size. &gt; 1 ⇒ trades
/// in the recent window are larger than the longer baseline ("institutional-sized" prints).</item>
/// </list>
/// The cube has 8 octants; v1 trades only the two clearest:
/// <list type="bullet">
/// <item><b>Institutional accumulation</b> (long): positive CVD ∧ buy-dominant aggressor ∧
/// larger-than-baseline size. Edge: large buyers stepping in over sustained period.</item>
/// <item><b>Institutional distribution</b> (short): mirror of the above.</item>
/// </list>
/// Exit when CVD reverses across the threshold against the position OR after
/// <see cref="HoldTrades"/> trades (time stop). Trades flagged <see cref="AggressorSide.Unknown"/>
/// contribute to the size baseline only — they carry no directional information.
///
/// <para>Note: CVD imbalance and aggressor ratio computed over the same window are
/// mathematically linked (cvd ≈ 2·aggressor − 1), so the entries effectively combine into a
/// single directional+magnitude filter. For true cube-style orthogonal signals, run the three
/// axes over different windows (e.g. short aggressor / medium CVD / long baseline) — left as a
/// tunable for iteration.</para>
/// </summary>
public sealed class OrderFlowCubeStrategy : IBacktestStrategy
{
    public int WindowTrades { get; }
    public int BaselineTrades { get; }
    public double CvdImbalanceThreshold { get; }
    public double AggressorBuyThreshold { get; }
    public double SizeRatioThreshold { get; }
    public int HoldTrades { get; }
    public long Quantity { get; }

    private readonly Contract _contract;
    private readonly Queue<(long Volume, bool IsBuy)> _window;
    private readonly Queue<long> _baseline;
    private long _windowBuyVol;
    private long _windowSellVol;
    private long _windowTotalVol;
    private long _baselineSizeSum;
    private long _position;
    private int _tradesHeld;
    private int _orderSeq;
    private int _lastEntrySign;

    public OrderFlowCubeStrategy(
        Contract contract,
        int windowTrades = 200,
        int baselineTrades = 2000,
        double cvdImbalanceThreshold = 0.40,
        double aggressorBuyThreshold = 0.60,
        double sizeRatioThreshold = 1.20,
        int holdTrades = 200,
        long quantity = 1)
    {
        _contract = contract;
        WindowTrades = windowTrades;
        BaselineTrades = baselineTrades;
        CvdImbalanceThreshold = cvdImbalanceThreshold;
        AggressorBuyThreshold = aggressorBuyThreshold;
        SizeRatioThreshold = sizeRatioThreshold;
        HoldTrades = holdTrades;
        Quantity = quantity;
        _window = new Queue<(long, bool)>(windowTrades);
        _baseline = new Queue<long>(baselineTrades);
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    // Quote-only updates carry no information for this strategy.
    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        // Every trade contributes to the size baseline, even Unknown-aggressor prints —
        // dropping them would bias the "average size" estimate.
        _baseline.Enqueue(trade.Size);
        _baselineSizeSum += trade.Size;
        while (_baseline.Count > BaselineTrades)
            _baselineSizeSum -= _baseline.Dequeue();

        var isBuy = trade.Aggressor == AggressorSide.Buy;
        var isSell = trade.Aggressor == AggressorSide.Sell;
        if (!isBuy && !isSell) return;

        _window.Enqueue((trade.Size, isBuy));
        if (isBuy) _windowBuyVol += trade.Size; else _windowSellVol += trade.Size;
        _windowTotalVol += trade.Size;
        while (_window.Count > WindowTrades)
        {
            var old = _window.Dequeue();
            if (old.IsBuy) _windowBuyVol -= old.Volume; else _windowSellVol -= old.Volume;
            _windowTotalVol -= old.Volume;
        }

        // Warm-up: need the full directional window and at least a quarter of the baseline
        // for the size ratio to be meaningful.
        if (_window.Count < WindowTrades) return;
        if (_baseline.Count < Math.Max(WindowTrades, BaselineTrades / 4)) return;
        if (_windowTotalVol <= 0 || _baselineSizeSum <= 0) return;

        var cvdImbalance = (double)(_windowBuyVol - _windowSellVol) / _windowTotalVol;
        var aggressorBuy = (double)_windowBuyVol / _windowTotalVol;
        var windowAvgSize = (double)_windowTotalVol / _window.Count;
        var baselineAvgSize = (double)_baselineSizeSum / _baseline.Count;
        var sizeRatio = windowAvgSize / baselineAvgSize;

        if (_position == 0)
        {
            // Institutional accumulation octant.
            if (cvdImbalance >= CvdImbalanceThreshold
                && aggressorBuy >= AggressorBuyThreshold
                && sizeRatio >= SizeRatioThreshold)
            {
                _position = Quantity;
                _tradesHeld = 0;
                _lastEntrySign = +1;
                await Submit(router, OrderSide.Buy, Quantity, ct).ConfigureAwait(false);
                return;
            }

            // Institutional distribution octant (mirror).
            if (cvdImbalance <= -CvdImbalanceThreshold
                && aggressorBuy <= 1.0 - AggressorBuyThreshold
                && sizeRatio >= SizeRatioThreshold)
            {
                _position = -Quantity;
                _tradesHeld = 0;
                _lastEntrySign = -1;
                await Submit(router, OrderSide.Sell, Quantity, ct).ConfigureAwait(false);
            }
            return;
        }

        _tradesHeld++;
        // Regime-reversal exit: CVD flipped past threshold against our position.
        var cvdSign = cvdImbalance >= CvdImbalanceThreshold ? +1
            : cvdImbalance <= -CvdImbalanceThreshold ? -1 : 0;
        var reversed = cvdSign != 0 && cvdSign != _lastEntrySign;

        if (reversed || _tradesHeld >= HoldTrades)
        {
            var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, exit, Math.Abs(_position), ct).ConfigureAwait(false);
            _position = 0;
            _lastEntrySign = 0;
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct).ConfigureAwait(false);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"ofcube-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
