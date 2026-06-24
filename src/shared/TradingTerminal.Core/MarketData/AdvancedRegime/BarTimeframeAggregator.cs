using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// Pure bar-to-bar timeframe aggregator. Resamples a time-ascending base bar series into wider
/// buckets (e.g. 1m base bars into 5m / 20m / 1D bars) by flooring each bar's open time to the
/// bucket boundary and folding OHLCV within each boundary.
/// </summary>
public static class BarTimeframeAggregator
{
    /// <summary>
    /// Aggregate <paramref name="baseBars"/> into <paramref name="bucket"/>-wide bars. Input is
    /// assumed time-ascending; output preserves that order. When the bucket is at or below the
    /// base spacing the input is returned unchanged (pass-through).
    /// </summary>
    public static IReadOnlyList<Bar> Aggregate(IReadOnlyList<Bar> baseBars, TimeSpan bucket)
    {
        if (baseBars is null || baseBars.Count == 0)
            return Array.Empty<Bar>();
        if (bucket <= TimeSpan.Zero)
            return baseBars;

        // Pass-through when the bucket equals (or is smaller than) the native bar spacing.
        if (baseBars.Count >= 2)
        {
            var nativeSpacing = baseBars[1].TimestampUtc - baseBars[0].TimestampUtc;
            if (nativeSpacing > TimeSpan.Zero && bucket <= nativeSpacing)
                return baseBars;
        }

        var result = new List<Bar>(baseBars.Count);

        DateTime currentBoundary = default;
        double open = 0, high = 0, low = 0, close = 0;
        long volume = 0;
        bool inBucket = false;

        foreach (var bar in baseBars)
        {
            var boundary = FloorToBucket(bar.TimestampUtc, bucket);

            if (!inBucket)
            {
                currentBoundary = boundary;
                open = bar.Open;
                high = bar.High;
                low = bar.Low;
                close = bar.Close;
                volume = bar.Volume;
                inBucket = true;
            }
            else if (boundary == currentBoundary)
            {
                if (bar.High > high) high = bar.High;
                if (bar.Low < low) low = bar.Low;
                close = bar.Close;
                volume += bar.Volume;
            }
            else
            {
                result.Add(new Bar(currentBoundary, open, high, low, close, volume));
                currentBoundary = boundary;
                open = bar.Open;
                high = bar.High;
                low = bar.Low;
                close = bar.Close;
                volume = bar.Volume;
            }
        }

        if (inBucket)
            result.Add(new Bar(currentBoundary, open, high, low, close, volume));

        return result;
    }

    private static DateTime FloorToBucket(DateTime timestampUtc, TimeSpan bucket)
    {
        if (bucket >= TimeSpan.FromDays(1))
            return new DateTime(timestampUtc.Year, timestampUtc.Month, timestampUtc.Day, 0, 0, 0, DateTimeKind.Utc);

        var ticks = timestampUtc.Ticks;
        var floored = ticks - (ticks % bucket.Ticks);
        return new DateTime(floored, timestampUtc.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : timestampUtc.Kind);
    }
}
