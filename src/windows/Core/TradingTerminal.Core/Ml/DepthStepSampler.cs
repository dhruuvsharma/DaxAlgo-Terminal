using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// Resamples an irregular stored depth-snapshot stream onto the predictor's fixed step grid for
/// warm-start training: at each step boundary in <c>(previous.Ts, current.Ts]</c> it emits a
/// last-observation-carried-forward <see cref="OrderBookStepSummary"/> built from the snapshot
/// that was live at that boundary. Gaps longer than <paramref name="maxGap"/> emit nothing — a
/// recording break must not be trained as "the book froze". Warm-start steps carry zero trade
/// flow and <c>TradeFlowValid = false</c>; the predictor's validity feature discounts them.
/// Streaming, pure, single-threaded.
/// </summary>
public sealed class DepthStepSampler
{
    private readonly TimeSpan _step;
    private readonly TimeSpan _maxGap;
    private readonly int _statsDepth;
    private readonly long _sweepSize;
    private DepthSnapshot? _previous;

    public DepthStepSampler(TimeSpan step, int statsDepth, long sweepSize, TimeSpan? maxGap = null)
    {
        if (step <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(step));
        if (statsDepth <= 0) throw new ArgumentOutOfRangeException(nameof(statsDepth));
        _step = step;
        _maxGap = maxGap ?? TimeSpan.FromSeconds(5);
        _statsDepth = statsDepth;
        _sweepSize = sweepSize;
    }

    /// <summary>The last step boundary a summary was emitted for — the caller's watermark against
    /// double-learning across the warm-start/live seam. <see cref="DateTime.MinValue"/> until the
    /// first emission.</summary>
    public DateTime LastBoundaryUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Feeds one snapshot (ascending timestamps) and appends any newly crossed step
    /// boundaries' summaries to <paramref name="output"/>. Returns the number emitted.</summary>
    public int Add(DepthSnapshot snapshot, List<OrderBookStepSummary> output)
    {
        var previous = _previous;
        _previous = snapshot;
        if (previous is null) return 0;

        var emitted = 0;
        if (snapshot.TimestampUtc - previous.TimestampUtc <= _maxGap)
        {
            var prevTicks = previous.TimestampUtc.Ticks;
            var boundary = new DateTime(prevTicks - prevTicks % _step.Ticks, DateTimeKind.Utc) + _step;
            while (boundary <= snapshot.TimestampUtc)
            {
                output.Add(OrderBookStepSummary.From(previous, _statsDepth, _sweepSize,
                    signedTradeFlow: 0, tradeCount: 0, tradeFlowValid: false, timestampUtc: boundary));
                LastBoundaryUtc = boundary;
                boundary += _step;
                emitted++;
            }
        }
        return emitted;
    }

    public void Reset()
    {
        _previous = null;
        LastBoundaryUtc = DateTime.MinValue;
    }
}
