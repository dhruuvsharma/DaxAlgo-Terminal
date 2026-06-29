namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// All configurable indicator lengths, thresholds and display toggles for the Advanced Live Market
/// Regime dashboard. Mutable so the settings panel can bind two-way; <see cref="Default"/> returns
/// a fresh instance with the canonical defaults each time.
/// </summary>
public sealed class AdvancedRegimeSettings
{
    // RSI
    public int RsiLength { get; set; } = 14;
    public double RsiOverbought { get; set; } = 70;
    public double RsiOversold { get; set; } = 30;

    // MACD
    public int MacdFast { get; set; } = 12;
    public int MacdSlow { get; set; } = 26;
    public int MacdSignal { get; set; } = 9;

    // CCI
    public int CciLength { get; set; } = 20;

    // Moving averages
    public int Ma9Length { get; set; } = 9;
    public int Ma21Length { get; set; } = 21;
    public int Ma50Length { get; set; } = 50;

    // Triple MA stack
    public int TripleMaFast { get; set; } = 8;
    public int TripleMaMid { get; set; } = 21;
    public int TripleMaSlow { get; set; } = 50;

    // Standard deviation
    public int StdLength { get; set; } = 20;

    // SuperTrend
    public double SuperTrendFactor { get; set; } = 3.0;
    public int SuperTrendAtrLength { get; set; } = 10;

    // ATR / ATR regression
    public int AtrLength { get; set; } = 14;
    public int AtrRegressionLength { get; set; } = 50;

    // Volume profile / range
    public int PocLookback { get; set; } = 50;
    public int TrendRangeLength { get; set; } = 20;

    // Delta / volume
    public int DeltaLookback { get; set; } = 20;

    // Per-row enabled flags (all on by default).
    public bool EnableRsi { get; set; } = true;
    public bool EnableMacd { get; set; } = true;
    public bool EnableCci { get; set; } = true;
    public bool EnableMa9 { get; set; } = true;
    public bool EnableMa21 { get; set; } = true;
    public bool EnableMa50 { get; set; } = true;
    public bool EnableTripleMa { get; set; } = true;
    public bool EnableVwap { get; set; } = true;
    public bool EnableSuperTrend { get; set; } = true;
    public bool EnableAtr { get; set; } = true;
    public bool EnableAtrRegression { get; set; } = true;
    public bool EnableStd { get; set; } = true;
    public bool EnablePocPosition { get; set; } = true;
    public bool EnableTrendRange { get; set; } = true;
    public bool EnableDelta { get; set; } = true;
    public bool EnableCumulativeDelta { get; set; } = true;
    public bool EnableVolumeBuySell { get; set; } = true;
    public bool EnableTrend { get; set; } = true;

    // Display toggles.
    public bool ShowValue { get; set; } = true;
    public bool ShowDirection { get; set; } = true;

    /// <summary>Whether the given row is enabled, by enum value.</summary>
    public bool IsRowEnabled(AdvancedIndicatorRow row) => row switch
    {
        AdvancedIndicatorRow.Rsi             => EnableRsi,
        AdvancedIndicatorRow.Macd            => EnableMacd,
        AdvancedIndicatorRow.Cci             => EnableCci,
        AdvancedIndicatorRow.Ma9             => EnableMa9,
        AdvancedIndicatorRow.Ma21            => EnableMa21,
        AdvancedIndicatorRow.Ma50            => EnableMa50,
        AdvancedIndicatorRow.TripleMa        => EnableTripleMa,
        AdvancedIndicatorRow.Vwap            => EnableVwap,
        AdvancedIndicatorRow.SuperTrend      => EnableSuperTrend,
        AdvancedIndicatorRow.Atr             => EnableAtr,
        AdvancedIndicatorRow.AtrRegression   => EnableAtrRegression,
        AdvancedIndicatorRow.Std             => EnableStd,
        AdvancedIndicatorRow.PocPosition     => EnablePocPosition,
        AdvancedIndicatorRow.TrendRange      => EnableTrendRange,
        AdvancedIndicatorRow.Delta           => EnableDelta,
        AdvancedIndicatorRow.CumulativeDelta => EnableCumulativeDelta,
        AdvancedIndicatorRow.VolumeBuySell   => EnableVolumeBuySell,
        AdvancedIndicatorRow.Trend           => EnableTrend,
        _ => false,
    };

    /// <summary>A fresh settings instance with the canonical defaults.</summary>
    public static AdvancedRegimeSettings Default => new();

    /// <summary>An independent copy. Every field is a value type, so a memberwise clone is a full deep
    /// copy — used where a caller needs per-instance settings (e.g. per-constituent overrides) that can
    /// be edited without affecting the shared default.</summary>
    public AdvancedRegimeSettings Clone() => (AdvancedRegimeSettings)MemberwiseClone();
}
