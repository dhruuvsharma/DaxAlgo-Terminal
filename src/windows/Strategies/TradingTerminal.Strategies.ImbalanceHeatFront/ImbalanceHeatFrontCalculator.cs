using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

/// <summary>
/// Live state for the Imbalance Heat Front strategy. Maintains a rolling [<see cref="NumSlices"/>
/// time slices × <see cref="NumLevels"/> distance-from-touch levels] matrix of imbalance ratios
/// in [-1, +1] and exposes ridge detection + ridge-trend (growing / shrinking) over a small
/// memory window so the VM can apply the momentum / mean-reversion logic.
///
/// <para>Each cell at distance <c>d</c> holds
/// <c>(bid_size_at_d − ask_size_at_d) / (bid_size_at_d + ask_size_at_d)</c>, computed off the
/// most recent <see cref="DepthSnapshot"/>. <c>d=0</c> is the touch (best bid vs best ask);
/// <c>d=i</c> compares the i-th depth row on each side. The level-pair definition is symmetric
/// about the mid by construction — the X-axis IS independent of the imbalance value, no
/// orthogonality issue (this is a surface, not a 3-axis cube).</para>
///
/// <para>Slice scheduling is event-driven, not wall-clock: each call to <see cref="OnDepth"/>
/// updates the current slice in place; every <see cref="EventsPerSlice"/> events the slice is
/// frozen into the rolling history and a fresh one begins. With L2 update rates of 5-20/sec on
/// liquid US equities, EventsPerSlice ≈ 10 ⇒ ~0.5-2s per slice ⇒ NumSlices=30 ⇒ 15-60s window,
/// matching the spec's "rolling, last 30-60 seconds" Y-axis.</para>
/// </summary>
public sealed class ImbalanceHeatFrontCalculator
{
    public int NumLevels { get; }
    public int NumSlices { get; }
    public int EventsPerSlice { get; }
    public double RidgeThreshold { get; }
    public int RidgeWidth { get; }
    public int RidgeMemorySlices { get; }

    private readonly Queue<double[]> _completed;
    private double[] _current;
    private int _eventsInCurrent;

    /// <summary>Sliding window of recent ridge heights (max |imbalance| across the ridge band).
    /// Drives the growing / shrinking decision for momentum vs mean-reversion entries.</summary>
    private readonly Queue<double> _recentRidgeHeights;

    public long DepthEventsSeen { get; private set; }

    public ImbalanceHeatFrontCalculator(
        int numLevels = 5,
        int numSlices = 30,
        int eventsPerSlice = 10,
        double ridgeThreshold = 0.75,
        int ridgeWidth = 3,
        int ridgeMemorySlices = 3)
    {
        if (numLevels < 1) throw new ArgumentOutOfRangeException(nameof(numLevels));
        if (numSlices < 2) throw new ArgumentOutOfRangeException(nameof(numSlices));
        if (eventsPerSlice < 1) throw new ArgumentOutOfRangeException(nameof(eventsPerSlice));
        if (ridgeThreshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ridgeThreshold), "Ridge threshold must be in (0, 1].");
        if (ridgeWidth < 1 || ridgeWidth > numLevels) throw new ArgumentOutOfRangeException(nameof(ridgeWidth));
        if (ridgeMemorySlices < 2) throw new ArgumentOutOfRangeException(nameof(ridgeMemorySlices));

        NumLevels = numLevels;
        NumSlices = numSlices;
        EventsPerSlice = eventsPerSlice;
        RidgeThreshold = ridgeThreshold;
        RidgeWidth = ridgeWidth;
        RidgeMemorySlices = ridgeMemorySlices;

        _completed = new Queue<double[]>(numSlices);
        _current = new double[numLevels];
        _recentRidgeHeights = new Queue<double>(ridgeMemorySlices);
    }

    public void Reset()
    {
        _completed.Clear();
        Array.Clear(_current);
        _eventsInCurrent = 0;
        _recentRidgeHeights.Clear();
        DepthEventsSeen = 0;
    }

    /// <summary>Side: +1 = bid-dominant ridge (book stacked under the mid), −1 = ask-dominant
    /// ridge (book stacked above the mid), 0 = no ridge. <see cref="Height"/> is the mean |imbalance|
    /// across the ridge band. <see cref="Trend"/> compares the latest ridge height to the older
    /// samples in the memory window: <c>+1</c> growing, <c>−1</c> shrinking, <c>0</c> steady or
    /// insufficient history.</summary>
    public readonly record struct RidgeState(int Side, double Height, int Trend, int StartLevel, int Width);

    public readonly record struct OnDepthResult(
        double[] LatestSlice,
        RidgeState Ridge,
        double NearTouchImbalance);

    public OnDepthResult OnDepth(DepthSnapshot depth)
    {
        DepthEventsSeen++;
        var slice = new double[NumLevels];
        for (var i = 0; i < NumLevels; i++)
        {
            var bidSize = i < depth.Bids.Count ? depth.Bids[i].Size : 0;
            var askSize = i < depth.Asks.Count ? depth.Asks[i].Size : 0;
            slice[i] = Imbalance(bidSize, askSize);
        }
        _current = slice;

        var ridge = DetectRidge(slice);
        // Track ridge height history regardless of side flips — a ridge that flips side resets
        // the trend. Zero (no ridge) also resets so post-dissolution ridges start fresh.
        var prevSide = _recentRidgeHeights.Count > 0 ? _previousSide : 0;
        if (ridge.Side == 0 || ridge.Side != prevSide)
        {
            _recentRidgeHeights.Clear();
        }
        _previousSide = ridge.Side;
        if (ridge.Side != 0)
        {
            _recentRidgeHeights.Enqueue(ridge.Height);
            while (_recentRidgeHeights.Count > RidgeMemorySlices) _recentRidgeHeights.Dequeue();
        }
        ridge = ridge with { Trend = ComputeTrend() };

        _eventsInCurrent++;
        if (_eventsInCurrent >= EventsPerSlice)
        {
            _completed.Enqueue(slice);
            while (_completed.Count > NumSlices - 1) _completed.Dequeue();
            _current = new double[NumLevels];
            _eventsInCurrent = 0;
        }

        return new OnDepthResult(slice, ridge, slice.Length > 0 ? slice[0] : 0);
    }

    private int _previousSide;

    /// <summary>Returns the surface in [NumSlices, NumLevels] row-major order. Row 0 is the
    /// OLDEST completed slice; the last row is the current accumulating slice. Pads with zero
    /// slices at the top when fewer than NumSlices−1 completed slices have been seen.</summary>
    public double[,] GetSurface()
    {
        var surface = new double[NumSlices, NumLevels];
        var completedArr = _completed.ToArray();
        var padRows = (NumSlices - 1) - completedArr.Length;
        for (var r = 0; r < completedArr.Length; r++)
        {
            var src = completedArr[r];
            for (var c = 0; c < NumLevels; c++) surface[padRows + r, c] = src[c];
        }
        for (var c = 0; c < NumLevels; c++) surface[NumSlices - 1, c] = _current[c];
        return surface;
    }

    private static double Imbalance(long bidSize, long askSize)
    {
        var total = (double)(bidSize + askSize);
        if (total <= 0) return 0;
        return (bidSize - askSize) / total;
    }

    private RidgeState DetectRidge(double[] slice)
    {
        var run = 0;
        var runSign = 0;
        var runSum = 0.0;
        var runStart = 0;
        var bestRun = 0;
        var bestSign = 0;
        var bestMean = 0.0;
        var bestStart = 0;
        for (var i = 0; i < slice.Length; i++)
        {
            var v = slice[i];
            if (Math.Abs(v) < RidgeThreshold)
            {
                run = 0; runSign = 0; runSum = 0;
                continue;
            }
            var sign = Math.Sign(v);
            if (sign == runSign)
            {
                run++; runSum += Math.Abs(v);
            }
            else
            {
                run = 1; runSign = sign; runSum = Math.Abs(v); runStart = i;
            }
            if (run >= RidgeWidth && run > bestRun)
            {
                bestRun = run;
                bestSign = runSign;
                bestMean = runSum / run;
                bestStart = runStart;
            }
        }
        return bestRun >= RidgeWidth
            ? new RidgeState(bestSign, bestMean, 0, bestStart, bestRun)
            : new RidgeState(0, 0, 0, 0, 0);
    }

    private int ComputeTrend()
    {
        if (_recentRidgeHeights.Count < 2) return 0;
        var arr = _recentRidgeHeights.ToArray();
        var latest = arr[^1];
        // Compare to the mean of the preceding samples — single-sample noise shouldn't flip the
        // trend label. Threshold 0.02 ≈ "2 percentage points of imbalance" — anything tighter is
        // visual noise on a [-1, 1] axis.
        var prevMean = 0.0;
        for (var i = 0; i < arr.Length - 1; i++) prevMean += arr[i];
        prevMean /= (arr.Length - 1);
        if (latest > prevMean + 0.02) return +1;
        if (latest < prevMean - 0.02) return -1;
        return 0;
    }
}
