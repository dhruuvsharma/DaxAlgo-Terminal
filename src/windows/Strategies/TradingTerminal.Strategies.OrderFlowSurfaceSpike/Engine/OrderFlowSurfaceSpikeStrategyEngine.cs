using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike.Engine;

/// <summary>
/// Order Flow Surface Spike detector. Maintains a rolling matrix [<see cref="NumSlices"/> time
/// slices × price bins] of signed trade volume (buy − sell), Z-score normalizes the whole
/// surface every tick, and looks for spikes in the LATEST slice — any cell whose |Z| breaches
/// <see cref="SpikeThreshold"/>. The intuition: an isolated cell standing far above its neighbors
/// in space and time is informed flow concentrating at a price level. Enter in the direction of
/// the spike (positive Z ⇒ buy pressure ⇒ long) after a confirmation window of
/// <see cref="ConfirmationTicks"/> consecutive same-direction breaches.
///
/// Exits: fixed TP/SL in price units (<see cref="StopLossPips"/> / <see cref="TakeProfitPips"/>),
/// OR immediate exit when the spike dissipates in the latest slice — z-magnitude drops below
/// threshold or flips sign against position. "Pips" here is a misnomer the user prefers — the
/// values are in raw price units; the caller picks whatever scale matches their instrument
/// (0.0001 for EURUSD, 0.25 for ES, 1.0 for BTC, etc.).
///
/// <para>Bins are absolute price levels keyed by <c>floor(price / PriceBinSize)</c> in a sparse
/// dictionary per slice — no re-centering, no bin window limit. Memory scales with the price
/// range traversed within <see cref="NumSlices"/> × <see cref="TicksPerSlice"/> ticks; bounded
/// in practice by per-slice tick volume.</para>
/// </summary>
public sealed class OrderFlowSurfaceSpikeStrategy : IBacktestStrategy
{
    public int TicksPerSlice { get; }
    public int NumSlices { get; }
    public double PriceBinSize { get; }
    public double SpikeThreshold { get; }
    public long Quantity { get; }
    public double StopLossPips { get; }
    public double TakeProfitPips { get; }
    public int ConfirmationTicks { get; }

    private readonly Contract _contract;
    private readonly Queue<Dictionary<long, double>> _completed;
    private Dictionary<long, double> _current;
    private int _ticksInCurrent;

    private int _candidateDirection;       // +1 long-candidate, -1 short-candidate, 0 none
    private int _candidateConsecutive;
    private long _position;
    private double _entryPrice;
    private int _orderSeq;

    public OrderFlowSurfaceSpikeStrategy(
        Contract contract,
        int ticksPerSlice = 100,
        int numSlices = 30,
        double priceBinSize = 0.05,
        double spikeThreshold = 2.5,
        long quantity = 1,
        double stopLossPips = 20,
        double takeProfitPips = 40,
        int confirmationTicks = 2)
    {
        if (ticksPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(ticksPerSlice));
        if (numSlices < 2) throw new ArgumentOutOfRangeException(nameof(numSlices));
        if (priceBinSize <= 0) throw new ArgumentOutOfRangeException(nameof(priceBinSize));
        if (spikeThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(spikeThreshold));
        if (confirmationTicks < 1) throw new ArgumentOutOfRangeException(nameof(confirmationTicks));

        _contract = contract;
        TicksPerSlice = ticksPerSlice;
        NumSlices = numSlices;
        PriceBinSize = priceBinSize;
        SpikeThreshold = spikeThreshold;
        Quantity = quantity;
        StopLossPips = stopLossPips;
        TakeProfitPips = takeProfitPips;
        ConfirmationTicks = confirmationTicks;

        _completed = new Queue<Dictionary<long, double>>(numSlices);
        _current = new Dictionary<long, double>();
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    // Pure trade-tape strategy — quotes don't contribute.
    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        var sign = trade.Aggressor switch
        {
            AggressorSide.Buy => 1,
            AggressorSide.Sell => -1,
            _ => 0,
        };
        if (sign != 0)
        {
            var bin = (long)Math.Floor(trade.Price / PriceBinSize);
            _current.TryGetValue(bin, out var v);
            _current[bin] = v + sign * trade.Size;
        }

        _ticksInCurrent++;
        if (_ticksInCurrent >= TicksPerSlice)
        {
            _completed.Enqueue(_current);
            while (_completed.Count > NumSlices - 1) _completed.Dequeue();
            _current = new Dictionary<long, double>();
            _ticksInCurrent = 0;
        }

        // TP/SL — checked on every tick regardless of spike state.
        if (_position != 0)
        {
            var diff = trade.Price - _entryPrice;
            var hitSl = _position > 0 ? diff <= -StopLossPips : diff >= StopLossPips;
            var hitTp = _position > 0 ? diff >= TakeProfitPips : diff <= -TakeProfitPips;
            if (hitSl || hitTp)
            {
                await ExitAsync(router, ct).ConfigureAwait(false);
                return;
            }
        }

        var spike = FindLatestSpike();
        if (spike is { } sp)
        {
            // Spike present.
            if (_candidateDirection == sp.dir) _candidateConsecutive++;
            else { _candidateDirection = sp.dir; _candidateConsecutive = 1; }

            if (_position == 0 && _candidateConsecutive >= ConfirmationTicks)
            {
                _position = sp.dir > 0 ? Quantity : -Quantity;
                _entryPrice = trade.Price;
                _candidateDirection = 0; _candidateConsecutive = 0;
                await Submit(router, sp.dir > 0 ? OrderSide.Buy : OrderSide.Sell, Quantity, ct).ConfigureAwait(false);
            }
            else if (_position != 0 && Math.Sign(_position) != sp.dir)
            {
                // Spike flipped against us — exit and let the next confirmation cycle re-arm.
                await ExitAsync(router, ct).ConfigureAwait(false);
            }
            return;
        }

        // No spike in latest slice — reset candidate and exit any open position (Z reverted).
        _candidateDirection = 0; _candidateConsecutive = 0;
        if (_position != 0)
            await ExitAsync(router, ct).ConfigureAwait(false);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        await ExitAsync(router, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the (bin, dir) of the highest-|Z| cell in the latest (currently
    /// accumulating) slice IF its |Z| ≥ threshold, otherwise null. Z-score uses mean+std across
    /// every cell in the matrix (all completed slices + the current one).</summary>
    private (long bin, int dir)? FindLatestSpike()
    {
        // Mean / std across all cells. Two passes — clarity over micro-perf for a 30-slice matrix.
        var count = 0; var sum = 0.0;
        foreach (var s in _completed) foreach (var v in s.Values) { sum += v; count++; }
        foreach (var v in _current.Values) { sum += v; count++; }
        if (count < 10) return null;

        var mean = sum / count;
        var sse = 0.0;
        foreach (var s in _completed) foreach (var v in s.Values) { var d = v - mean; sse += d * d; }
        foreach (var v in _current.Values) { var d = v - mean; sse += d * d; }
        var std = Math.Sqrt(sse / count);
        if (std < 1e-12) return null;

        long? bestBin = null; var bestZ = 0.0;
        foreach (var (b, v) in _current)
        {
            var z = (v - mean) / std;
            if (Math.Abs(z) > Math.Abs(bestZ)) { bestZ = z; bestBin = b; }
        }
        if (bestBin is null || Math.Abs(bestZ) < SpikeThreshold) return null;
        return (bestBin.Value, Math.Sign(bestZ));
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
            ClientOrderId: $"ofss-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);
}
