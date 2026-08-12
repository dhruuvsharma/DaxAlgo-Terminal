using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// Cheap per-sealed-bar summary the footprint predictor trains on. Built once when a bar seals
/// from the Core <see cref="FootprintBar"/> plus the argmax buy/sell POCs and value-area bounds
/// the render layer already computes. ~100 bytes, so the predictor's internal history ring is
/// independent of (and much deeper than) the window's small visible-bar cap.
/// </summary>
/// <param name="StartUtc">Bar open time (UTC).</param>
/// <param name="Poc">Total-volume point of control (argmax row price).</param>
/// <param name="BuyPoc">Argmax buy-volume row price (NaN when the bar had no buy volume).</param>
/// <param name="SellPoc">Argmax sell-volume row price (NaN when the bar had no sell volume).</param>
/// <param name="High">Highest row price in the bar (NaN when empty).</param>
/// <param name="Low">Lowest row price in the bar (NaN when empty).</param>
/// <param name="ValueAreaHigh">Top of the 70% value area (NaN when empty).</param>
/// <param name="ValueAreaLow">Bottom of the 70% value area (NaN when empty).</param>
/// <param name="TotalVolume">Total traded volume in the bar.</param>
/// <param name="Delta">Bar delta (buy − sell volume).</param>
/// <param name="CumulativeDelta">Running cumulative delta through this bar.</param>
/// <param name="StackedBuy">Longest stacked ask-imbalance run.</param>
/// <param name="StackedSell">Longest stacked bid-imbalance run.</param>
/// <param name="QualityMultiplier">Feed-quality trust multiplier q ∈ [0, 1].</param>
public sealed record FootprintBarSummary(
    DateTime StartUtc,
    double Poc,
    double BuyPoc,
    double SellPoc,
    double High,
    double Low,
    double ValueAreaHigh,
    double ValueAreaLow,
    long TotalVolume,
    long Delta,
    long CumulativeDelta,
    int StackedBuy,
    int StackedSell,
    double QualityMultiplier)
{
    /// <summary>Projects a sealed Core bar (plus the render-layer argmax POCs and value area)
    /// into the predictor's summary shape. Rows are high → low per the Core convention.</summary>
    public static FootprintBarSummary From(
        FootprintBar bar, double buyPoc, double sellPoc, double valueAreaHigh, double valueAreaLow)
    {
        var rows = bar.Rows;
        var high = rows.Count > 0 ? rows[0].Price : double.NaN;
        var low = rows.Count > 0 ? rows[^1].Price : double.NaN;
        return new FootprintBarSummary(
            bar.StartUtc, bar.PocPrice, buyPoc, sellPoc, high, low, valueAreaHigh, valueAreaLow,
            bar.TotalVolume, bar.Delta, bar.CumulativeDelta, bar.StackedBuy, bar.StackedSell,
            bar.Quality.Multiplier());
    }
}

/// <summary>One ML-forecast future bar at horizon <paramref name="Horizon"/> (bars past the last
/// sealed bar). Prices are absolute; <paramref name="TotalVolume"/> and <paramref name="Delta"/>
/// are reconstructed from the model's relative targets, so they are estimates in volume units.</summary>
public sealed record FootprintForecastBar(
    int Horizon,
    double Poc,
    double BuyPoc,
    double SellPoc,
    double TotalVolume,
    double Delta);

/// <summary>Rolling accuracy read-out for one 1-step-ahead POC forecaster (ML or baseline).
/// <paramref name="PocMaeTicks"/> and <paramref name="DirectionalHitRate"/> are over the rolling
/// metrics window; <paramref name="ScoredCount"/> is the lifetime number of scored forecasts.</summary>
public readonly record struct ForecastAccuracy(
    double PocMaeTicks,
    double DirectionalHitRate,
    long ScoredCount);

/// <summary>Tuning for <see cref="FootprintNextBarPredictor"/>.</summary>
/// <param name="MaxHorizon">Hard cap on the ML forecast horizon in bars. Direct per-horizon
/// learners are trained up to here; the regression overlay's longer horizons stay curve-fit only.</param>
/// <param name="HistoryCapacity">Depth of the internal sealed-bar summary ring.</param>
/// <param name="MetricsWindow">Rolling window for MAE / hit-rate.</param>
/// <param name="Lambda">RLS exponential-forgetting factor.</param>
/// <param name="MinSamplesReady">Per-horizon RLS updates required before forecasts are emitted.</param>
/// <param name="Learner">Which online-learner algorithm the per-(target × horizon) bank uses.</param>
public sealed record FootprintPredictorOptions(
    int MaxHorizon = 8,
    int HistoryCapacity = 512,
    int MetricsWindow = 100,
    double Lambda = 0.995,
    int MinSamplesReady = 20,
    LearnerKind Learner = LearnerKind.Rls);
