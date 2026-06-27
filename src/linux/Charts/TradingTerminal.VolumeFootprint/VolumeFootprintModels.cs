using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant;

namespace TradingTerminal.VolumeFootprint;

/// <summary>Which POC series an overlay fit curve belongs to (drives the brush choice).</summary>
public enum PocSeries { Total, Buy, Sell }

/// <summary>
/// How each price cell is rendered inside a footprint column.
/// <list type="bullet">
/// <item><see cref="BidAsk"/> — the classic split: sell volume left (red), buy volume right (green).</item>
/// <item><see cref="Delta"/> — one cell coloured by net delta sign, intensity by |delta|.</item>
/// <item><see cref="Volume"/> — one cell as a total-volume profile, intensity by total volume.</item>
/// </list>
/// </summary>
public enum CellDisplayMode { BidAsk, Delta, Volume }

/// <summary>One fitted overlay curve: ŷ price per column for one fit kind × POC series. When the
/// predictor is on the array extends past the visible bars by the prediction horizon.
/// Produced by the VM (<c>RecomputeFitCurves</c>), drawn by the window code-behind.</summary>
public sealed record PocFitCurve(CurveFitKind Kind, PocSeries Series, IReadOnlyList<double> Prices);

/// <summary>One virtual (predicted) column: per-series consensus prices — the mean of every
/// enabled fit kind extrapolated to that future column. NaN where a series had no valid fit.</summary>
public sealed record PredictedBar(double Poc, double BuyPoc, double SellPoc);

/// <summary>
/// WPF render model for one footprint bar. Wraps the immutable <see cref="FootprintBar"/>
/// produced by <see cref="FootprintFeatures.BuildBar"/> and adds the two argmax-per-side POC
/// prices that the overlay needs. Core supplies BuyCentroid and SellCentroid (VWAP per side),
/// but the connector lines want the argmax row — so those two fields are computed locally from
/// the bar's Rows once when the bar is produced and cached here.
/// </summary>
public sealed class RenderBar
{
    /// <summary>Fraction of bar volume that defines the value area (market-profile convention, 70%).</summary>
    private const double ValueAreaFraction = 0.70;

    public RenderBar(FootprintBar core)
    {
        Core = core;

        // Argmax buy POC: row with the largest BuyVolume.
        long bestBuy = 0;
        long bestSell = 0;
        long maxCell = 0;
        BuyPointOfControl = double.NaN;
        SellPointOfControl = double.NaN;
        foreach (var row in core.Rows)
        {
            if (row.BuyVolume > bestBuy)  { bestBuy  = row.BuyVolume;  BuyPointOfControl  = row.Price; }
            if (row.SellVolume > bestSell) { bestSell = row.SellVolume; SellPointOfControl = row.Price; }
            if (row.TotalVolume > maxCell) maxCell = row.TotalVolume;
        }
        MaxCellVolume = maxCell;

        (ValueAreaHigh, ValueAreaLow) = ComputeValueArea(core);
    }

    /// <summary>
    /// Market-profile value area: starting from the POC row, repeatedly annex whichever adjacent
    /// row (above or below the running band) carries more volume until the band holds
    /// <see cref="ValueAreaFraction"/> of the bar's total volume. Returns (VAH, VAL) prices, or
    /// (NaN, NaN) when the bar is empty. Rows are high → low (Core convention).
    /// </summary>
    private static (double High, double Low) ComputeValueArea(FootprintBar core)
    {
        var rows = core.Rows;
        if (rows.Count == 0 || core.TotalVolume <= 0) return (double.NaN, double.NaN);

        var pocIdx = 0;
        long best = -1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].TotalVolume > best) { best = rows[i].TotalVolume; pocIdx = i; }

        var target = (long)Math.Ceiling(core.TotalVolume * ValueAreaFraction);
        long acc = rows[pocIdx].TotalVolume;
        int hi = pocIdx, lo = pocIdx; // hi = higher price (smaller index), lo = lower price (larger index)
        while (acc < target && (hi > 0 || lo < rows.Count - 1))
        {
            var volAbove = hi > 0 ? rows[hi - 1].TotalVolume : -1;
            var volBelow = lo < rows.Count - 1 ? rows[lo + 1].TotalVolume : -1;
            if (volAbove < 0 && volBelow < 0) break;
            if (volBelow > volAbove) { lo++; acc += rows[lo].TotalVolume; }
            else { hi--; acc += rows[hi].TotalVolume; }
        }
        return (rows[hi].Price, rows[lo].Price);
    }

    /// <summary>The canonical bar produced by Core's stateless extractor.</summary>
    public FootprintBar Core { get; }

    // ── Forwarded properties used by the window code-behind ────────────────────────────────

    public DateTime StartUtc      => Core.StartUtc;
    public long     TotalVolume   => Core.TotalVolume;
    public long     Delta         => Core.Delta;
    public long     CumulativeDelta => Core.CumulativeDelta;

    /// <summary>Total-volume point of control (argmax total-volume row price), from Core.</summary>
    public double PointOfControl  => Core.PocPrice;

    /// <summary>Argmax buy-volume row price. Computed locally from <see cref="FootprintBar.Rows"/>
    /// because Core exposes BuyCentroid (VWAP), not an argmax.</summary>
    public double BuyPointOfControl  { get; }

    /// <summary>Argmax sell-volume row price. Same convention as <see cref="BuyPointOfControl"/>.</summary>
    public double SellPointOfControl { get; }

    /// <summary>Highest price in the 70% value area (NaN when the bar is empty).</summary>
    public double ValueAreaHigh { get; }

    /// <summary>Lowest price in the 70% value area (NaN when the bar is empty).</summary>
    public double ValueAreaLow { get; }

    /// <summary>Largest single-row total volume in this bar — the per-bar intensity normaliser.</summary>
    public long MaxCellVolume { get; }

    /// <summary>Longest run of consecutive ask-imbalanced (stacked-buying) rows, from Core.</summary>
    public int StackedBuy => Core.StackedBuy;

    /// <summary>Longest run of consecutive bid-imbalanced (stacked-selling) rows, from Core.</summary>
    public int StackedSell => Core.StackedSell;

    /// <summary>Price rows ordered high → low, as returned by Core.</summary>
    public IReadOnlyList<Core.MarketData.FootprintFeatureRow> Cells => Core.Rows;

    /// <summary>Feed quality tag from the Core bar (RealTape or SyntheticL1).</summary>
    public FeedQuality Quality => Core.Quality;
}
