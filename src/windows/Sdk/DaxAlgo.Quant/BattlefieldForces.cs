using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Shared layout for a NewHedge / Hyperion-style order-book battlefield: each resting size chunk
/// becomes a soldier on the price it rests at; bids are bulls (negative X), asks are bears
/// (positive X); the mid is the front line.
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

        var mid = (depth.BestBid + depth.BestAsk) * 0.5;
        var soldiers = new List<Soldier>(128);
        PlaceSide(soldiers, depth.Bids, mid, tick, halfWidthTicks, troopUnit, maxTroopsPerLevel, isBid: true);
        PlaceSide(soldiers, depth.Asks, mid, tick, halfWidthTicks, troopUnit, maxTroopsPerLevel, isBid: false);
        return soldiers;
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
            // Strike flies from the aggressor's side toward the mid.
            var fromX = t.IsBuy ? Math.Min(x, -0.15) : Math.Max(x, 0.15);
            list.Add(new Strike(fromX, 0.08, 0.1, 0, 0.12, -0.05, t.IsBuy, t.Size));
        }
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
                // Stack troops slightly up and back so walls read as columns of soldiers.
                var y = 0.02d + (i * 0.045d);
                var z = (i % 3) * 0.04d - 0.04d;
                into.Add(new Soldier(x, y, z, isBid, level.Size / (double)troops, level.Price));
            }
        }
    }
}
