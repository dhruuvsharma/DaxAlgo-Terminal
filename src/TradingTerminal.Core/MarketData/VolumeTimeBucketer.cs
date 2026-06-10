using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// One constant-volume bucket: trade prints accumulated until the bucket's volume reaches the
/// target size B. The VPIN / order-flow-toxicity literature (Easley-López de Prado-O'Hara 2012)
/// partitions the tape into equal-<em>volume</em> rather than equal-<em>time</em> buckets so each
/// observation carries comparable information; this record is the per-bucket output.
/// </summary>
/// <param name="StartUtc">Time of the first print in the bucket.</param>
/// <param name="EndUtc">Time of the print that closed the bucket.</param>
/// <param name="BuyVolume">Buy-initiated volume in the bucket.</param>
/// <param name="SellVolume">Sell-initiated volume in the bucket.</param>
/// <param name="Vwap">Volume-weighted average price across the bucket's prints.</param>
public sealed record VolumeBucket(
    DateTime StartUtc,
    DateTime EndUtc,
    long BuyVolume,
    long SellVolume,
    double Vwap)
{
    /// <summary>Total volume in the bucket (≈ the target size B, modulo the closing print).</summary>
    public long TotalVolume => BuyVolume + SellVolume;

    /// <summary>Buy fraction f = buyVol / totalVol ∈ [0, 1]. The per-bucket VPIN ingredient.</summary>
    public double BuyFraction => TotalVolume > 0 ? (double)BuyVolume / TotalVolume : 0.5;

    /// <summary>Bucket delta: buy minus sell volume.</summary>
    public long Delta => BuyVolume - SellVolume;
}

/// <summary>
/// Stateless volume-time bucketer: partitions a trade stream into constant-volume buckets of
/// target size B. Pure C#, deterministic. When a print would overflow the current bucket it is
/// <em>not</em> split — the bucket closes and the whole print starts the next one — keeping
/// per-print aggressor attribution intact (the standard simplification; over- vs under-fill is
/// bounded by the largest print and averages out).
/// </summary>
public static class VolumeTimeBucketer
{
    /// <summary>
    /// Partitions <paramref name="prints"/> into constant-volume buckets of target size
    /// <paramref name="bucketVolume"/> B. The trailing partial bucket is emitted only when
    /// <paramref name="includePartial"/> is true (default false — match the VPIN convention of
    /// dropping the incomplete bucket).
    /// </summary>
    public static IEnumerable<VolumeBucket> Bucketize(
        IEnumerable<FootprintPrint> prints,
        long bucketVolume,
        bool includePartial = false)
    {
        if (bucketVolume <= 0) throw new ArgumentOutOfRangeException(nameof(bucketVolume));
        ArgumentNullException.ThrowIfNull(prints);

        long buy = 0, sell = 0, total = 0;
        double notional = 0;
        DateTime start = default, end = default;
        var open = false;

        foreach (var p in prints)
        {
            if (p.Size <= 0) continue;
            if (!open) { start = p.TimeUtc; open = true; }
            end = p.TimeUtc;

            switch (p.Aggressor)
            {
                case AggressorSide.Buy: buy += p.Size; break;
                case AggressorSide.Sell: sell += p.Size; break;
                default:
                    var half = p.Size / 2;
                    buy += p.Size - half;
                    sell += half;
                    break;
            }
            total += p.Size;
            notional += p.Price * p.Size;

            if (total >= bucketVolume)
            {
                yield return new VolumeBucket(start, end, buy, sell, notional / total);
                buy = sell = total = 0;
                notional = 0;
                open = false;
            }
        }

        if (open && includePartial && total > 0)
            yield return new VolumeBucket(start, end, buy, sell, notional / total);
    }

    /// <summary>
    /// Adaptive bucket size B = median bar volume / 4. Robust to single fat bars (median, not
    /// mean). Returns at least 1. The "/4" gives roughly four buckets per bar so the toxicity
    /// estimate refreshes intra-bar without becoming noise-dominated.
    /// </summary>
    public static long AdaptiveBucketVolume(IReadOnlyList<long> barVolumes)
    {
        ArgumentNullException.ThrowIfNull(barVolumes);
        var med = Median(barVolumes);
        return Math.Max(1, (long)Math.Round(med / 4.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// VPIN-convention bucket size B = total daily volume / 50 (Easley-López de Prado-O'Hara use
    /// 50 buckets per day). Returns at least 1.
    /// </summary>
    public static long VpinBucketVolume(long dailyVolume)
    {
        if (dailyVolume <= 0) return 1;
        return Math.Max(1, (long)Math.Round(dailyVolume / 50.0, MidpointRounding.AwayFromZero));
    }

    private static double Median(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
