using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Per-subscription local order book for exchanges that stream an initial snapshot followed by
/// incremental updates (Coinbase level2, Bybit orderbook, Kraken book). Maintains price→size maps
/// (bids descending, asks ascending), applies updates (size 0 = remove the level), and projects the
/// top-N levels into a canonical <see cref="DepthSnapshot"/> with sizes scaled to the integer size
/// fields. Not thread-safe — each <c>SubscribeDepthAsync</c> enumeration owns its own instance and
/// drives it from a single reader loop.
/// </summary>
internal sealed class L2OrderBook
{
    private readonly SortedDictionary<double, double> _bids =
        new(Comparer<double>.Create((a, b) => b.CompareTo(a))); // highest first
    private readonly SortedDictionary<double, double> _asks = new(); // lowest first

    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
    }

    /// <summary>Apply one level. <paramref name="size"/> &lt;= 0 removes the price level.</summary>
    public void Apply(bool isBid, double price, double size)
    {
        var book = isBid ? _bids : _asks;
        if (size <= 0) book.Remove(price);
        else book[price] = size;
    }

    public bool IsEmpty => _bids.Count == 0 && _asks.Count == 0;

    public DepthSnapshot Snapshot(int levels, double sizeScale, DateTime? timeUtc = null)
    {
        levels = Math.Max(1, levels);
        return new DepthSnapshot(
            timeUtc ?? DateTime.UtcNow,
            Project(_bids, levels, sizeScale),
            Project(_asks, levels, sizeScale));
    }

    private static IReadOnlyList<DepthLevel> Project(SortedDictionary<double, double> side, int levels, double sizeScale)
    {
        var result = new List<DepthLevel>(levels);
        foreach (var kv in side)
        {
            result.Add(new DepthLevel(kv.Key, (long)Math.Round(kv.Value * sizeScale)));
            if (result.Count >= levels) break;
        }
        return result;
    }
}
