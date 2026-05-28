namespace TradingTerminal.Core.IndexKScore;

/// <summary>
/// Configuration for the per-component K-score computation. All windows / lookbacks /
/// thresholds for the 15 directional indicators live here; defaults match the strategy spec.
/// The 15 weights MUST sum to 1.0 (±<see cref="WeightSumTolerance"/>) or
/// <see cref="IndexKScoreCalculator"/> throws on construction.
/// </summary>
public sealed record IndexKScoreParameters
{
    public const double WeightSumTolerance = 0.001;

    // Indicator windows.
    public int RsiLength { get; init; } = 14;
    public double RsiOverbought { get; init; } = 70;
    public double RsiOversold { get; init; } = 30;
    public int MacdFast { get; init; } = 12;
    public int MacdSlow { get; init; } = 26;
    public int MacdSignal { get; init; } = 9;
    public int CciLength { get; init; } = 20;
    public int Ma9Length { get; init; } = 9;
    public int Ma21Length { get; init; } = 21;
    public int Ma50Length { get; init; } = 50;
    public int Ma3Fast { get; init; } = 8;
    public int Ma3Mid { get; init; } = 21;
    public int Ma3Slow { get; init; } = 50;
    public bool VwapSession { get; init; } = true;
    public double SupertrendFactor { get; init; } = 3.0;
    public int SupertrendAtrLength { get; init; } = 10;
    public int AtrLength { get; init; } = 14;
    public int AtrRegLength { get; init; } = 50;
    public int StdLength { get; init; } = 20;
    public int PocLookback { get; init; } = 50;
    public int TrdLength { get; init; } = 20;
    public int DeltaLookback { get; init; } = 20;

    // Indicator weights (15 directional indicators; spec calls this "16" but enumerates 15).
    // Defaults from spec — must sum to 1.0.
    public double WeightSuperTrend { get; init; } = 0.12;
    public double WeightMacd { get; init; } = 0.11;
    public double WeightRsi { get; init; } = 0.10;
    public double WeightVwap { get; init; } = 0.09;
    public double Weight3Ma { get; init; } = 0.09;
    public double WeightCumDelta { get; init; } = 0.08;
    public double WeightVolBs { get; init; } = 0.08;
    public double WeightCci { get; init; } = 0.07;
    public double WeightMa50 { get; init; } = 0.06;
    public double WeightMa21 { get; init; } = 0.05;
    public double WeightPocPos { get; init; } = 0.05;
    public double WeightTrd { get; init; } = 0.04;
    public double WeightMa9 { get; init; } = 0.03;
    public double WeightDelta { get; init; } = 0.02;
    public double WeightAtrReg { get; init; } = 0.01;

    public double WeightSum =>
        WeightSuperTrend + WeightMacd + WeightRsi + WeightVwap + Weight3Ma +
        WeightCumDelta + WeightVolBs + WeightCci + WeightMa50 + WeightMa21 +
        WeightPocPos + WeightTrd + WeightMa9 + WeightDelta + WeightAtrReg;

    public void Validate()
    {
        if (Math.Abs(WeightSum - 1.0) > WeightSumTolerance)
            throw new ArgumentException(
                $"Indicator weights must sum to 1.0 (±{WeightSumTolerance}); current sum is {WeightSum:F4}.");
        if (RsiLength < 2) throw new ArgumentException("RsiLength must be >= 2.");
        if (MacdFast < 1 || MacdSlow <= MacdFast) throw new ArgumentException("MacdSlow must be > MacdFast >= 1.");
        if (MacdSignal < 1) throw new ArgumentException("MacdSignal must be >= 1.");
        if (CciLength < 2) throw new ArgumentException("CciLength must be >= 2.");
        if (Ma9Length < 1 || Ma21Length < 1 || Ma50Length < 1) throw new ArgumentException("MA lengths must be >= 1.");
        if (Ma3Fast < 1 || Ma3Mid <= Ma3Fast || Ma3Slow <= Ma3Mid)
            throw new ArgumentException("3MA stack requires fast < mid < slow.");
        if (SupertrendFactor <= 0) throw new ArgumentException("SupertrendFactor must be > 0.");
        if (SupertrendAtrLength < 2) throw new ArgumentException("SupertrendAtrLength must be >= 2.");
        if (AtrLength < 2 || AtrRegLength < 2 || StdLength < 2)
            throw new ArgumentException("ATR/ATR-reg/STD lengths must be >= 2.");
        if (PocLookback < 2 || TrdLength < 2 || DeltaLookback < 1)
            throw new ArgumentException("POC/TRD/Delta lookbacks must satisfy minimum thresholds.");
    }
}
