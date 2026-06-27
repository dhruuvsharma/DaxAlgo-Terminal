using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

/// <summary>
/// Live state for the Order Flow Surface Spike strategy. Maintains a rolling matrix of
/// <see cref="NumSlices"/> time slices × sparse price bins of signed trade volume
/// (buy − sell), and exposes Z-score normalized views for visualization and spike detection.
/// One instance per streamed instrument.
///
/// <para>Bins are absolute price levels keyed by <c>floor(price / PriceBinSize)</c>. The
/// surface view is a fixed-width window centered on the most recent trade's bin.</para>
/// </summary>
public sealed class OrderFlowSurfaceCalculator
{
    public int TicksPerSlice { get; }
    public int NumSlices { get; }
    public double PriceBinSize { get; }
    public int WindowBins { get; }

    private readonly Queue<Dictionary<long, double>> _completed;
    private Dictionary<long, double> _current;
    private int _ticksInCurrent;
    private long _latestBin;
    private bool _hasLatestBin;

    public long TicksSeen { get; private set; }

    public OrderFlowSurfaceCalculator(
        int ticksPerSlice = 100,
        int numSlices = 30,
        double priceBinSize = 0.05,
        int windowBins = 41)
    {
        if (ticksPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(ticksPerSlice));
        if (numSlices < 2) throw new ArgumentOutOfRangeException(nameof(numSlices));
        if (priceBinSize <= 0) throw new ArgumentOutOfRangeException(nameof(priceBinSize));
        if (windowBins < 5 || windowBins % 2 == 0) throw new ArgumentOutOfRangeException(nameof(windowBins), "windowBins must be odd and >= 5.");

        TicksPerSlice = ticksPerSlice;
        NumSlices = numSlices;
        PriceBinSize = priceBinSize;
        WindowBins = windowBins;

        _completed = new Queue<Dictionary<long, double>>(numSlices);
        _current = new Dictionary<long, double>();
    }

    public void Reset()
    {
        _completed.Clear();
        _current.Clear();
        _ticksInCurrent = 0;
        _hasLatestBin = false;
        TicksSeen = 0;
    }

    /// <summary>Result of <see cref="Add"/> — the new surface state plus any detected spike
    /// in the latest slice (bin + signed Z) once the threshold check is run by the caller.</summary>
    public readonly record struct AddResult(
        long LatestBin,
        double Mean,
        double Std,
        long? SpikeBin,
        double SpikeZ);

    public AddResult Add(TradePrint trade, double spikeThreshold)
    {
        TicksSeen++;
        var sign = trade.Aggressor switch
        {
            AggressorSide.Buy => 1,
            AggressorSide.Sell => -1,
            _ => 0,
        };
        var bin = (long)Math.Floor(trade.Price / PriceBinSize);
        _latestBin = bin;
        _hasLatestBin = true;

        if (sign != 0)
        {
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

        var (mean, std) = ComputeStats();
        long? spikeBin = null;
        var spikeZ = 0.0;
        if (std > 1e-12)
        {
            foreach (var (b, v) in _current)
            {
                var z = (v - mean) / std;
                if (Math.Abs(z) > Math.Abs(spikeZ)) { spikeZ = z; spikeBin = b; }
            }
            if (spikeBin is null || Math.Abs(spikeZ) < spikeThreshold)
            { spikeBin = null; spikeZ = 0; }
        }

        return new AddResult(bin, mean, std, spikeBin, spikeZ);
    }

    /// <summary>Returns a Z-score normalized rectangular view of the surface, sized
    /// <c>[NumSlices, WindowBins]</c>. The window is centered on the most recent trade's bin
    /// (or 0 if no trade has been seen yet). Row 0 is the OLDEST completed slice; row
    /// <c>NumSlices-1</c> is the current accumulating slice. Returns all-zero when there's
    /// not enough data for std &gt; 0.</summary>
    public double[,] GetZScoreSurface()
    {
        var surface = new double[NumSlices, WindowBins];
        if (!_hasLatestBin) return surface;
        var (mean, std) = ComputeStats();
        if (std < 1e-12) return surface;

        var half = WindowBins / 2;
        var centerBin = _latestBin;
        var firstBin = centerBin - half;

        // Completed slices occupy rows 0..(NumSlices - 2). Pad with zeros at the top when we
        // haven't accumulated NumSlices - 1 completed slices yet.
        var completedArr = _completed.ToArray();
        var padRows = (NumSlices - 1) - completedArr.Length;
        for (var r = 0; r < completedArr.Length; r++)
        {
            var dict = completedArr[r];
            var rowIdx = padRows + r;
            for (var c = 0; c < WindowBins; c++)
            {
                dict.TryGetValue(firstBin + c, out var v);
                surface[rowIdx, c] = (v - mean) / std;
            }
        }
        // Current accumulating slice goes on the last row.
        for (var c = 0; c < WindowBins; c++)
        {
            _current.TryGetValue(firstBin + c, out var v);
            surface[NumSlices - 1, c] = (v - mean) / std;
        }
        return surface;
    }

    public long LatestBin => _hasLatestBin ? _latestBin : 0;

    private (double mean, double std) ComputeStats()
    {
        var count = 0; var sum = 0.0;
        foreach (var s in _completed) foreach (var v in s.Values) { sum += v; count++; }
        foreach (var v in _current.Values) { sum += v; count++; }
        if (count == 0) return (0, 0);
        var mean = sum / count;
        var sse = 0.0;
        foreach (var s in _completed) foreach (var v in s.Values) { var d = v - mean; sse += d * d; }
        foreach (var v in _current.Values) { var d = v - mean; sse += d * d; }
        return (mean, Math.Sqrt(sse / count));
    }
}
