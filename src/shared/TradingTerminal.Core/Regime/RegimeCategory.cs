namespace TradingTerminal.Core.Regime;

/// <summary>
/// The ten sub-signals that blend into the composite market-regime score. Each is scored
/// 0–100 (0 = maximum fear / risk-off, 100 = maximum greed / risk-on) and weighted; see
/// <see cref="MarketRegimeCalculator.Weight"/>.
/// </summary>
public enum RegimeCategory
{
    /// <summary>Survey + index sentiment (CNN Fear &amp; Greed, AAII bull/bear).</summary>
    Sentiment,

    /// <summary>VIX level and term structure (VIX vs VIX3M: contango/backwardation).</summary>
    Volatility,

    /// <summary>Options positioning — put/call ratio and SKEW.</summary>
    Positioning,

    /// <summary>Price relative to its 20/50/200-day moving averages.</summary>
    Trend,

    /// <summary>Market breadth — % of S&amp;P above 200d, equal-weight vs cap-weight.</summary>
    Breadth,

    /// <summary>Index ROC plus average sector RSI.</summary>
    Momentum,

    /// <summary>Liquidity — M2 YoY, Fed balance sheet MoM, SOFR.</summary>
    Liquidity,

    /// <summary>Credit spreads — high-yield and investment-grade OAS plus their trend.</summary>
    Credit,

    /// <summary>Macro — Fed funds, 10y–2y curve, unemployment.</summary>
    Macro,

    /// <summary>Cross-asset confirmation — gold, long bonds, and the dollar vs equities.</summary>
    CrossAsset,
}
