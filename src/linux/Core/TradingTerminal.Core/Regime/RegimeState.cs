namespace TradingTerminal.Core.Regime;

/// <summary>
/// The five standard Fear &amp; Greed bands the composite falls into. Boundaries follow the
/// CNN convention (≤20 Extreme Fear, ≤40 Fear, ≤60 Neutral, ≤80 Greed, else Extreme Greed).
/// </summary>
public enum RegimeState
{
    ExtremeFear,
    Fear,
    Neutral,
    Greed,
    ExtremeGreed,
}

public static class RegimeStateExtensions
{
    /// <summary>Maps a 0–100 composite score to its band.</summary>
    public static RegimeState FromScore(double score) => score switch
    {
        <= 20 => RegimeState.ExtremeFear,
        <= 40 => RegimeState.Fear,
        <= 60 => RegimeState.Neutral,
        <= 80 => RegimeState.Greed,
        _ => RegimeState.ExtremeGreed,
    };

    /// <summary>Human label used in the UI and notifications ("Extreme Fear", "Greed", …).</summary>
    public static string Label(this RegimeState state) => state switch
    {
        RegimeState.ExtremeFear => "Extreme Fear",
        RegimeState.Fear => "Fear",
        RegimeState.Neutral => "Neutral",
        RegimeState.Greed => "Greed",
        RegimeState.ExtremeGreed => "Extreme Greed",
        _ => "Unknown",
    };

    /// <summary>True for the fearful bands — used by the signal gate to decide risk-off.</summary>
    public static bool IsRiskOff(this RegimeState state) =>
        state is RegimeState.ExtremeFear or RegimeState.Fear;
}
