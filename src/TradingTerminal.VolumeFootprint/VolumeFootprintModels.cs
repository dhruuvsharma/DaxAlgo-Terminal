namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// One price level inside a footprint bar: the volume that traded at this price bucket split by
/// aggressor side. <see cref="Total"/> drives the point-of-control pick; <see cref="Delta"/> drives
/// the per-cell colour (buy-dominant green, sell-dominant red).
/// </summary>
public sealed class FootprintCell
{
    public FootprintCell(double price) => Price = price;

    /// <summary>Bucket centre price (snapped to the configured tick size).</summary>
    public double Price { get; }

    /// <summary>Volume that lifted the offer (buy-initiated) at this level.</summary>
    public long BuyVolume { get; private set; }

    /// <summary>Volume that hit the bid (sell-initiated) at this level.</summary>
    public long SellVolume { get; private set; }

    public long Total => BuyVolume + SellVolume;
    public long Delta => BuyVolume - SellVolume;

    public void AddBuy(long size) => BuyVolume += size;
    public void AddSell(long size) => SellVolume += size;
}

/// <summary>
/// A single footprint (cluster) bar — all trades inside one time bucket, bucketed by price into
/// <see cref="Cells"/>. Aggregates the bar delta, total volume, traded high/low and the
/// point-of-control (highest-volume price). Mutated live as trades arrive while the bar is forming.
/// </summary>
public sealed class FootprintBar
{
    private readonly SortedDictionary<long, FootprintCell> _cells = new();
    private readonly double _tickSize;

    public FootprintBar(DateTime startUtc, double tickSize)
    {
        StartUtc = startUtc;
        _tickSize = tickSize > 0 ? tickSize : 0.25;
    }

    public DateTime StartUtc { get; }

    public long TotalVolume { get; private set; }
    public long Delta { get; private set; }

    /// <summary>Running cumulative delta across all bars up to and including this one. Set by the VM
    /// when the bar is appended so the footer can show a CVD trend without re-summing.</summary>
    public long CumulativeDelta { get; set; }

    public double High { get; private set; } = double.NaN;
    public double Low { get; private set; } = double.NaN;
    public double Open { get; private set; } = double.NaN;
    public double Close { get; private set; } = double.NaN;

    /// <summary>Price level with the most total volume (point of control), or NaN for an empty bar.</summary>
    public double PointOfControl { get; private set; } = double.NaN;

    /// <summary>Cells ordered high price → low price (the order they render top-to-bottom).</summary>
    public IEnumerable<FootprintCell> Cells => _cells.Values.Reverse();

    public bool IsEmpty => _cells.Count == 0;

    public void Add(double price, long size, bool isBuy)
    {
        if (size <= 0) return;
        var bucketKey = (long)Math.Round(price / _tickSize, MidpointRounding.AwayFromZero);
        var bucketPrice = bucketKey * _tickSize;

        if (!_cells.TryGetValue(bucketKey, out var cell))
        {
            cell = new FootprintCell(bucketPrice);
            _cells[bucketKey] = cell;
        }
        if (isBuy) cell.AddBuy(size); else cell.AddSell(size);

        TotalVolume += size;
        Delta += isBuy ? size : -size;
        if (double.IsNaN(Open)) Open = price;
        Close = price;
        if (double.IsNaN(High) || price > High) High = price;
        if (double.IsNaN(Low) || price < Low) Low = price;

        // Recompute POC incrementally — cheap, bars are bounded by a few hundred levels at most.
        long best = -1; double pocPrice = double.NaN;
        foreach (var c in _cells.Values)
            if (c.Total > best) { best = c.Total; pocPrice = c.Price; }
        PointOfControl = pocPrice;
    }
}
