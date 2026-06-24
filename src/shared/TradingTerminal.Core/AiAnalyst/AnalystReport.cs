namespace TradingTerminal.Core.AiAnalyst;

/// <summary>
/// One indicator agent's read on the tape — text summary plus the structured numbers
/// the LLM looked at (RSI, MACD, ATR, etc.). Numbers are open-ended so the Python side
/// can add new indicators without a C# rev.
/// </summary>
public sealed record IndicatorReport(
    string Summary,
    IReadOnlyDictionary<string, double> Values);

/// <summary>
/// The pattern agent's verdict: which (if any) classical chart pattern fits the last
/// N bars, plus confidence (0..1) and the vision-LLM's reasoning.
/// </summary>
public sealed record PatternReport(
    string PatternName,
    double Confidence,
    string Reasoning);

/// <summary>
/// Fitted trend channel plus the trend agent's narrative. Slope is in price units per
/// bar; channel is the band the vision-LLM scored against.
/// </summary>
public sealed record TrendReport(
    string Direction,
    double Slope,
    double ChannelUpper,
    double ChannelLower,
    string Reasoning);

/// <summary>
/// The structured verdict shape returned by the Python sidecar. Every field is required
/// — the Python side validates against a Pydantic model and retries the LLM up to 3
/// times if the response doesn't parse. The two chart fields carry base64 PNG bytes so
/// the WPF view can bind them straight into Image controls.
/// </summary>
public sealed record AnalystReport(
    AiAnalystDecision Decision,
    string ForecastHorizon,
    double RiskRewardRatio,
    double Confidence,
    string Justification,
    IndicatorReport Indicator,
    PatternReport Pattern,
    TrendReport Trend,
    string PatternChartPngBase64,
    string TrendChartPngBase64,
    long ElapsedMs)
{
    /// <summary>
    /// Sentinel returned by <c>NullAiAnalystClient</c> and by any failure path inside
    /// the HTTP client. The UI's empty-state binds to this — same shape, no charts,
    /// decision is NoCall.
    /// </summary>
    public static AnalystReport Unavailable(string reason) => new(
        Decision: AiAnalystDecision.NoCall,
        ForecastHorizon: "—",
        RiskRewardRatio: 0,
        Confidence: 0,
        Justification: reason,
        Indicator: new IndicatorReport("AI Analyst unavailable", new Dictionary<string, double>()),
        Pattern: new PatternReport("None", 0, "AI Analyst unavailable"),
        Trend: new TrendReport("Flat", 0, 0, 0, "AI Analyst unavailable"),
        PatternChartPngBase64: string.Empty,
        TrendChartPngBase64: string.Empty,
        ElapsedMs: 0);
}
