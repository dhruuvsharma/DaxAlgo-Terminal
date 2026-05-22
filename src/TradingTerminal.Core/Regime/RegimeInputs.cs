namespace TradingTerminal.Core.Regime;

/// <summary>
/// Raw, source-agnostic inputs the <see cref="MarketRegimeCalculator"/> blends into a
/// snapshot. Price series are daily closes oldest→newest (empty array = unavailable);
/// FRED series are observation values oldest→newest. Scalars are null when their source
/// failed. This keeps the calculator pure and trivially unit-testable — the Infrastructure
/// fetchers' only job is to populate this record.
/// </summary>
public sealed class RegimeInputs
{
    // --- Yahoo: index + volatility ---
    public double? Vix { get; init; }
    public double? Vix9d { get; init; }
    public double? Vix3m { get; init; }
    public double? Skew { get; init; }
    public double[] SpxCloses { get; init; } = Array.Empty<double>();

    // --- Yahoo: breadth / cross-asset ETFs ---
    public double[] SpyCloses { get; init; } = Array.Empty<double>();
    public double[] RspCloses { get; init; } = Array.Empty<double>();
    public double[] GldCloses { get; init; } = Array.Empty<double>();
    public double[] TltCloses { get; init; } = Array.Empty<double>();
    public double[] DxyCloses { get; init; } = Array.Empty<double>();
    public double? HygPrice { get; init; }
    public double? TltPrice { get; init; }

    /// <summary>Sector ETF close series keyed by symbol (XLK, XLF, …) for the momentum RSI average.</summary>
    public IReadOnlyDictionary<string, double[]> SectorCloses { get; init; } =
        new Dictionary<string, double[]>();

    // --- Options positioning ---
    public double? PutCallRatio { get; init; }

    // --- Breadth header ---
    public double? PctAbove200dma { get; init; }

    // --- Sentiment ---
    public int? CnnFearGreed { get; init; }
    public double? AaiiBull { get; init; }
    public double? AaiiBear { get; init; }

    // --- FRED observation series (oldest→newest) ---
    public double[] HighYieldOas { get; init; } = Array.Empty<double>();   // BAMLH0A0HYM2
    public double[] InvGradeOas { get; init; } = Array.Empty<double>();    // BAMLC0A0CM
    public double[] M2 { get; init; } = Array.Empty<double>();             // M2SL (weekly)
    public double[] FedBalanceSheet { get; init; } = Array.Empty<double>();// WALCL (weekly)
    public double[] FedFunds { get; init; } = Array.Empty<double>();       // FEDFUNDS
    public double[] Curve10y2y { get; init; } = Array.Empty<double>();     // T10Y2Y
    public double[] Unemployment { get; init; } = Array.Empty<double>();   // UNRATE
    public double[] Yield10y { get; init; } = Array.Empty<double>();       // DGS10
    public double? Sofr { get; init; }                                     // SOFR latest
}
