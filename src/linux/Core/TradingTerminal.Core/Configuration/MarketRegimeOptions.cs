namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Market Regime composite — a 0–100 risk-on/risk-off score blended from
/// ten weighted sub-signals (volatility, positioning, trend, breadth, momentum, credit,
/// liquidity, macro, sentiment, cross-asset). Inputs come from free public endpoints:
/// Yahoo Finance charts (always on), the FRED REST API (needs a free key), CNN's Fear &amp;
/// Greed dataviz endpoint, and the AAII sentiment survey. Any source that is missing or
/// fails degrades that category to a neutral 50 rather than failing the whole composite.
///
/// Mirrors the algorithm in the upstream <c>worldmonitor</c> project's
/// <c>seed-fear-greed.mjs</c>, reimplemented behind <see cref="MarketData"/>-free Core types.
/// </summary>
public sealed class MarketRegimeOptions
{
    public const string SectionName = "MarketRegime";

    /// <summary>Master switch. When false the service does not poll and the panel shows "disabled".</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to recompute the composite. The upstream seed runs every 6h; intraday
    /// users want it tighter. Clamped to a 5-minute floor in the refresh loop to stay polite to
    /// the free endpoints (Yahoo throttles aggressive callers).</summary>
    public int RefreshMinutes { get; set; } = 30;

    /// <summary>FRED API key (free at https://fred.stlouisfed.org/docs/api/api_key.html). Enables
    /// the credit, liquidity, and macro categories plus the 10y/HY header metrics. Without it
    /// those categories degrade to neutral.</summary>
    public string FredApiKey { get; set; } = string.Empty;

    /// <summary>Pull CNN's Fear &amp; Greed index as a sentiment input. Free, no key, but a
    /// scraped endpoint that can break — disable if it gets noisy.</summary>
    public bool UseCnnFearGreed { get; set; } = true;

    /// <summary>Pull the weekly AAII bull/bear sentiment survey. Scraped HTML; lowest reliability,
    /// always non-blocking.</summary>
    public bool UseAaiiSentiment { get; set; } = true;

    /// <summary>Emit a notification (via the configured transports) when the regime crosses
    /// between risk bands — e.g. Greed → Fear.</summary>
    public bool NotifyOnRegimeChange { get; set; } = true;

    /// <summary>When true, strategy <c>Signal</c> notifications are suppressed while the regime
    /// is risk-off (composite below <see cref="RiskOffThreshold"/>). The signal still appears in
    /// the strategy's own Signals list — only the outbound alert is gated.</summary>
    public bool GateSignalsWhenRiskOff { get; set; }

    /// <summary>Composite score at or below which the market is considered risk-off for gating
    /// purposes. 40 is the Fear/Neutral boundary in the standard 0–100 banding.</summary>
    public double RiskOffThreshold { get; set; } = 40;
}
