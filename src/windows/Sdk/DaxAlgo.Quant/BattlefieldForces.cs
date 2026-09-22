using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Shared layout for a NewHedge / Hyperion-style order-book battlefield: each resting size chunk
/// becomes a soldier on the price it rests at; bids are bulls (negative X), asks are bears
/// (positive X); the mid is the front line. War-ground merge stacks recent books so persistent
/// walls read as thicker armies — the short path to “multi-venue mesh” without a second renderer.
/// </summary>
public static class BattlefieldForces
{
    public readonly record struct Soldier(
        double X,
        double Y,
        double Z,
        bool IsBid,
        double Size,
        double Price);

    public readonly record struct Strike(
        double X0,
        double Y0,
        double Z0,
        double X1,
        double Y1,
        double Z1,
        bool IsBuy,
        double Size);

    /// <param name="troopUnit">Size per soldier (ceil(level.Size / troopUnit) troops, capped).</param>
    /// <param name="maxTroopsPerLevel">Hard cap so a whale wall cannot flood the scene.</param>
    public static IReadOnlyList<Soldier> FromDepth(
        DepthSnapshot depth,
        double tick,
        int halfWidthTicks = 24,
        double troopUnit = 5,
        int maxTroopsPerLevel = 12)
    {
        ArgumentNullException.ThrowIfNull(depth);
        if (tick <= 0 || !double.IsFinite(depth.BestBid) || !double.IsFinite(depth.BestAsk))
            return Array.Empty<Soldier>();
        if (depth.BestBid <= 0 || depth.BestAsk <= 0)
            return Array.Empty<Soldier>();

        var mid = (depth.BestBid + depth.BestAsk) * 0.5;
        var soldiers = new List<Soldier>(128);
        PlaceSide(soldiers, depth.Bids, mid, tick, halfWidthTicks, troopUnit, maxTroopsPerLevel, isBid: true);
        PlaceSide(soldiers, depth.Asks, mid, tick, halfWidthTicks, troopUnit, maxTroopsPerLevel, isBid: false);
        return soldiers;
    }

    /// <summary>
    /// War-ground merge: sum resting size at the same tick-bucket across several books
    /// (recent heatmap columns, or multiple venues). Persistent walls → thicker armies.
    /// </summary>
    public static DepthSnapshot MergeBooks(IReadOnlyList<DepthSnapshot> books, double tick)
    {
        ArgumentNullException.ThrowIfNull(books);
        if (books.Count == 0 || tick <= 0)
            return new DepthSnapshot(DateTime.UtcNow, Array.Empty<DepthLevel>(), Array.Empty<DepthLevel>());

        var pairs = new (IReadOnlyList<DepthLevel> Bids, IReadOnlyList<DepthLevel> Asks)[books.Count];
        var time = books[0].TimestampUtc;
        for (var i = 0; i < books.Count; i++)
        {
            pairs[i] = (books[i].Bids, books[i].Asks);
            if (books[i].TimestampUtc > time) time = books[i].TimestampUtc;
        }

        return MergeBooksAtTick(pairs, tick, time);
    }

    /// <summary>Merge raw bid/ask sides at a tick grid.</summary>
    public static DepthSnapshot MergeBooksAtTick(
        IReadOnlyList<(IReadOnlyList<DepthLevel> Bids, IReadOnlyList<DepthLevel> Asks)> books,
        double tick,
        DateTime timeUtc)
    {
        ArgumentNullException.ThrowIfNull(books);
        if (books.Count == 0 || tick <= 0)
            return new DepthSnapshot(timeUtc, Array.Empty<DepthLevel>(), Array.Empty<DepthLevel>());

        var bids = new Dictionary<long, long>();
        var asks = new Dictionary<long, long>();
        foreach (var book in books)
        {
            Accumulate(bids, book.Bids, tick);
            Accumulate(asks, book.Asks, tick);
        }

        return new DepthSnapshot(
            timeUtc,
            LevelsFromTicks(bids, tick, descending: true),
            LevelsFromTicks(asks, tick, descending: false));
    }

    /// <summary>Bid-size share in [0,1] across all soldiers (army pressure). 0.5 = even front.</summary>
    public static double BidPressure(IReadOnlyList<Soldier> soldiers)
    {
        if (soldiers is null || soldiers.Count == 0) return 0.5d;
        double bid = 0, ask = 0;
        foreach (var s in soldiers)
        {
            if (s.IsBid) bid += s.Size;
            else ask += s.Size;
        }
        var total = bid + ask;
        return total < Num.Epsilon ? 0.5d : bid / total;
    }

    /// <summary>Large aggressive prints as strikes toward the front line (mid).</summary>
    public static IReadOnlyList<Strike> FromTrades(
        IEnumerable<(double Price, long Size, bool IsBuy)> trades,
        double mid,
        double tick,
        int halfWidthTicks,
        long minSize)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (tick <= 0 || halfWidthTicks <= 0) return Array.Empty<Strike>();

        var list = new List<Strike>();
        foreach (var t in trades)
        {
            if (t.Size < minSize || !double.IsFinite(t.Price)) continue;
            var ticks = (t.Price - mid) / tick;
            var x = Math.Clamp(ticks / halfWidthTicks, -1d, 1d);
            var fromX = t.IsBuy ? Math.Min(x, -0.15) : Math.Max(x, 0.15);
            list.Add(new Strike(fromX, 0.08, 0.1, 0, 0.12, -0.05, t.IsBuy, t.Size));
        }
        return list;
    }

    private static void Accumulate(Dictionary<long, long> into, IReadOnlyList<DepthLevel> side, double tick)
    {
        foreach (var level in side)
        {
            if (!double.IsFinite(level.Price) || level.Size <= 0) continue;
            var key = (long)Math.Round(level.Price / tick);
            into[key] = into.TryGetValue(key, out var cur) ? cur + level.Size : level.Size;
        }
    }

    private static DepthLevel[] LevelsFromTicks(Dictionary<long, long> map, double tick, bool descending)
    {
        var keys = map.Keys.ToList();
        keys.Sort();
        if (descending) keys.Reverse();
        var list = new DepthLevel[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            list[i] = new DepthLevel(keys[i] * tick, map[keys[i]]);
        return list;
    }

    private static void PlaceSide(
        List<Soldier> into,
        IReadOnlyList<DepthLevel> side,
        double mid,
        double tick,
        int halfWidth,
        double troopUnit,
        int maxTroops,
        bool isBid)
    {
        var unit = Math.Max(1d, troopUnit);
        foreach (var level in side)
        {
            if (!double.IsFinite(level.Price) || level.Size <= 0) continue;
            var ticks = (level.Price - mid) / tick;
            if (Math.Abs(ticks) > halfWidth) continue;

            var x = Math.Clamp(ticks / halfWidth, -1d, 1d);
            var troops = Math.Clamp((int)Math.Ceiling(level.Size / unit), 1, maxTroops);
            for (var i = 0; i < troops; i++)
            {
                // Rank lines on Z (trenches) + stack on Y so walls read as columns.
                var y = 0.02d + (i * 0.045d);
                var z = ((i % 4) - 1.5d) * 0.08d;
                into.Add(new Soldier(x, y, z, isBid, level.Size / (double)troops, level.Price));
            }
        }
    }
}
