using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant;

namespace TradingTerminal.VolumeFootprint;

/// <summary>Which POC series an overlay fit curve belongs to (drives the brush choice).</summary>
public enum PocSeries { Total, Buy, Sell }

/// <summary>One fitted overlay curve: ŷ price per visible column for one fit kind × POC series.
/// Produced by the VM (<c>RecomputeFitCurves</c>), drawn by the window code-behind.</summary>
public sealed record PocFitCurve(CurveFitKind Kind, PocSeries Series, IReadOnlyList<double> Prices);

/// <summary>
/// WPF render model for one footprint bar. Wraps the immutable <see cref="FootprintBar"/>
/// produced by <see cref="FootprintFeatures.BuildBar"/> and adds the two argmax-per-side POC
/// prices that the overlay needs. Core supplies BuyCentroid and SellCentroid (VWAP per side),
/// but the connector lines want the argmax row — so those two fields are computed locally from
/// the bar's Rows once when the bar is produced and cached here.
/// </summary>
public sealed class RenderBar
{
    public RenderBar(FootprintBar core)
    {
        Core = core;

        // Argmax buy POC: row with the largest BuyVolume.
        long bestBuy = 0;
        long bestSell = 0;
        BuyPointOfControl = double.NaN;
        SellPointOfControl = double.NaN;
        foreach (var row in core.Rows)
        {
            if (row.BuyVolume > bestBuy)  { bestBuy  = row.BuyVolume;  BuyPointOfControl  = row.Price; }
            if (row.SellVolume > bestSell) { bestSell = row.SellVolume; SellPointOfControl = row.Price; }
        }
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

    /// <summary>Price rows ordered high → low, as returned by Core.</summary>
    public IReadOnlyList<Core.MarketData.FootprintFeatureRow> Cells => Core.Rows;

    /// <summary>Feed quality tag from the Core bar (RealTape or SyntheticL1).</summary>
    public FeedQuality Quality => Core.Quality;
}
