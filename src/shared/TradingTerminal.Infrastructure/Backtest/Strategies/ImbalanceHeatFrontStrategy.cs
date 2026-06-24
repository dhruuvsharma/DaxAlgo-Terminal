using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Imbalance Heat Front. Maintains a rolling matrix of per-distance L2 imbalance ratios over
/// the last <see cref="NumSlices"/> time slices and looks for a <em>ridge</em> — a band of
/// <see cref="RidgeWidth"/>+ consecutive distance levels where |imbalance| ≥ <see cref="RidgeThreshold"/>
/// — that persists for <see cref="ConfirmationSlices"/> consecutive samples on the same side.
/// A confirmed ridge means the book is one-sided across multiple levels, not just at the touch;
/// the next tick is far more likely to continue in that direction than chance would predict.
///
/// <para>Two execution modes (<see cref="Mode"/>):
/// <list type="bullet">
/// <item><b>Momentum</b> — enter <em>with</em> the ridge (long when bids dominate). Best when
/// the ridge is growing in height. Exit on ridge dissolution, sign flip, or TP/SL.</item>
/// <item><b>MeanReversion</b> — enter <em>against</em> the ridge (short when bids dominate)
/// on the thesis that one-sided books exhaust and snap back. Best when the ridge is decreasing
/// (exhaustion). Exit on ridge expansion against position, sign flip, or TP/SL.</item>
/// </list></para>
///
/// <para><b>Backtest caveat:</b> the engine is L1-only today (no replayed depth). When run in
/// the backtester, only the touch level (distance=1, i.e. best bid vs best ask) carries real
/// size, so ridge detection degenerates to a single-cell threshold check on best-bid-vs-best-ask
/// imbalance. The live UI (<c>TradingTerminal.Strategies.ImbalanceHeatFront</c>) consumes real
/// L2 depth via <see cref="IBacktestStrategy.OnDepthAsync"/> for the full ridge logic.</para>
/// </summary>
public sealed class ImbalanceHeatFrontStrategy : IBacktestStrategy
{
    public enum RidgeMode { Momentum, MeanReversion }

    public int NumLevels { get; }
    public int NumSlices { get; }
    public double RidgeThreshold { get; }
    public int RidgeWidth { get; }
    public int ConfirmationSlices { get; }
    public RidgeMode Mode { get; }
    public long Quantity { get; }
    public double StopLossPips { get; }
    public double TakeProfitPips { get; }

    private readonly Contract _contract;
    private readonly Queue<double[]> _slices;
    private double[] _current;
    private int _ticksInCurrentSlice;
    private readonly int _ticksPerSlice;

    private int _candidateDirection;
    private int _candidateConsecutive;
    private double _previousRidgeHeight;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public ImbalanceHeatFrontStrategy(
        Contract contract,
        int numLevels = 5,
        int numSlices = 30,
        double ridgeThreshold = 0.75,
        int ridgeWidth = 3,
        int confirmationSlices = 2,
        int ticksPerSlice = 20,
        RidgeMode mode = RidgeMode.Momentum,
        long quantity = 1,
        double stopLossPips = 2,
        double takeProfitPips = 4)
    {
        if (numLevels < 1) throw new ArgumentOutOfRangeException(nameof(numLevels));
        if (numSlices < 2) throw new ArgumentOutOfRangeException(nameof(numSlices));
        if (ridgeThreshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ridgeThreshold), "Ridge threshold must be in (0, 1].");
        if (ridgeWidth < 1 || ridgeWidth > numLevels) throw new ArgumentOutOfRangeException(nameof(ridgeWidth));
        if (confirmationSlices < 1) throw new ArgumentOutOfRangeException(nameof(confirmationSlices));
        if (ticksPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(ticksPerSlice));

        _contract = contract;
        NumLevels = numLevels;
        NumSlices = numSlices;
        RidgeThreshold = ridgeThreshold;
        RidgeWidth = ridgeWidth;
        ConfirmationSlices = confirmationSlices;
        _ticksPerSlice = ticksPerSlice;
        Mode = mode;
        Quantity = quantity;
        StopLossPips = stopLossPips;
        TakeProfitPips = takeProfitPips;

        _slices = new Queue<double[]>(numSlices);
        _current = new double[numLevels];
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (tick.Bid <= 0 || tick.Ask <= 0) return;

        // L1-only fallback: only distance 0 (the touch) has real size; everything else is 0.
        // Ridge detection degenerates to a single-cell threshold check.
        var touchImbalance = ComputeImbalance(tick.BidSize, tick.AskSize);
        var slice = new double[NumLevels];
        slice[0] = touchImbalance;
        AccumulateSlice(slice);

        await ProcessAsync(tick.Bid, tick.Ask, tick.TimestampUtc, router, ct).ConfigureAwait(false);
    }

    public async Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (depth.Bids.Count == 0 || depth.Asks.Count == 0) return;

        var slice = new double[NumLevels];
        for (var i = 0; i < NumLevels; i++)
        {
            var bidSize = i < depth.Bids.Count ? depth.Bids[i].Size : 0;
            var askSize = i < depth.Asks.Count ? depth.Asks[i].Size : 0;
            slice[i] = ComputeImbalance(bidSize, askSize);
        }
        AccumulateSlice(slice);

        await ProcessAsync(depth.BestBid, depth.BestAsk, depth.TimestampUtc, router, ct).ConfigureAwait(false);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        await ExitAsync(router, ct).ConfigureAwait(false);
    }

    private static double ComputeImbalance(long bidSize, long askSize)
    {
        var total = (double)(bidSize + askSize);
        if (total <= 0) return 0;
        return (bidSize - askSize) / total;
    }

    /// <summary>Folds a new instantaneous slice into the rolling buffer. L1 path can flush every
    /// tick; L2 path is event-driven (one depth snapshot = one slice update). The current slice is
    /// overwritten in place rather than averaged — the surface displays the most recent value per
    /// cell, matching the spec's "sample the DOM every 100 ms" semantics.</summary>
    private void AccumulateSlice(double[] slice)
    {
        _current = slice;
        _ticksInCurrentSlice++;
        if (_ticksInCurrentSlice >= _ticksPerSlice)
        {
            _slices.Enqueue(_current);
            while (_slices.Count > NumSlices - 1) _slices.Dequeue();
            _current = new double[NumLevels];
            _ticksInCurrentSlice = 0;
        }
    }

    private async Task ProcessAsync(double bid, double ask, DateTime _, IOrderRouter router, CancellationToken ct)
    {
        var price = (bid + ask) * 0.5;

        if (_position != 0)
        {
            var diff = price - _entryPrice;
            var hitSl = _position > 0 ? diff <= -StopLossPips : diff >= StopLossPips;
            var hitTp = _position > 0 ? diff >= TakeProfitPips : diff <= -TakeProfitPips;
            if (hitSl || hitTp)
            {
                await ExitAsync(router, ct).ConfigureAwait(false);
                return;
            }
        }

        var ridge = DetectRidge();
        if (ridge is { } r)
        {
            // For momentum mode, enter with the ridge (long if bid-dominant). For mean-reversion,
            // enter against it. The confirmation counter still tracks the ridge side, not the
            // trade side, so the algo doesn't second-guess itself within one confirmation cycle.
            if (_candidateDirection == r.side) _candidateConsecutive++;
            else { _candidateDirection = r.side; _candidateConsecutive = 1; }

            var tradeDir = Mode == RidgeMode.Momentum ? r.side : -r.side;
            var ridgeGrowing = r.height > _previousRidgeHeight + 1e-6;
            var ridgeShrinking = r.height < _previousRidgeHeight - 1e-6;

            // Mean-reversion only enters when the ridge is shrinking (exhaustion).
            // Momentum prefers growing ridges but accepts steady ones after confirmation.
            var triggerOk = Mode switch
            {
                RidgeMode.Momentum => ridgeGrowing || _candidateConsecutive >= ConfirmationSlices,
                RidgeMode.MeanReversion => ridgeShrinking && _candidateConsecutive >= ConfirmationSlices,
                _ => false,
            };

            if (_position == 0 && _candidateConsecutive >= ConfirmationSlices && triggerOk)
            {
                _position = tradeDir > 0 ? Quantity : -Quantity;
                _entryPrice = price;
                _candidateDirection = 0; _candidateConsecutive = 0;
                await Submit(router, tradeDir > 0 ? OrderSide.Buy : OrderSide.Sell, Quantity, ct).ConfigureAwait(false);
            }
            else if (_position != 0 && Math.Sign(_position) != tradeDir)
            {
                await ExitAsync(router, ct).ConfigureAwait(false);
            }

            _previousRidgeHeight = r.height;
            return;
        }

        // No ridge — reset confirmation. Open positions flatten on ridge dissolution.
        _candidateDirection = 0; _candidateConsecutive = 0;
        _previousRidgeHeight = 0;
        if (_position != 0)
            await ExitAsync(router, ct).ConfigureAwait(false);
    }

    /// <summary>Scans the latest (currently accumulating) slice for a band of
    /// <see cref="RidgeWidth"/>+ contiguous same-sign cells, each with |imbalance| ≥ threshold.
    /// Returns the side (+1 bid-heavy / −1 ask-heavy) and the mean |imbalance| across the band
    /// (its "height"). Returns null when no qualifying band exists.</summary>
    private (int side, double height)? DetectRidge()
    {
        var run = 0;
        var runSign = 0;
        var runSum = 0.0;
        var bestRun = 0;
        var bestSign = 0;
        var bestMean = 0.0;
        for (var i = 0; i < NumLevels; i++)
        {
            var v = _current[i];
            if (Math.Abs(v) < RidgeThreshold) { run = 0; runSign = 0; runSum = 0; continue; }
            var sign = Math.Sign(v);
            if (sign == runSign) { run++; runSum += Math.Abs(v); }
            else { run = 1; runSign = sign; runSum = Math.Abs(v); }
            if (run >= RidgeWidth && run > bestRun)
            {
                bestRun = run;
                bestSign = runSign;
                bestMean = runSum / run;
            }
        }
        return bestRun >= RidgeWidth ? (bestSign, bestMean) : null;
    }

    private Task ExitAsync(IOrderRouter router, CancellationToken ct)
    {
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        var qty = Math.Abs(_position);
        _position = 0;
        return Submit(router, side, qty, ct);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"ihf-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
