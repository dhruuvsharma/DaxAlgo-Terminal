namespace TradingTerminal.Core.Configuration;

/// <summary>
/// OANDA v20 configuration.
///
/// <para>Hosts come in pairs — practice and live are different machines, not a flag on one — and the
/// pair is split again between trading and streaming. Getting that wrong is the classic v20 mistake: a
/// token issued on practice returns 401 against the live host, which reads as a bad token rather than
/// as the wrong environment.</para>
///
/// <para>The token itself is never here. It lives in the DPAPI credential store like every other
/// broker secret; this holds only what is safe to sit in <c>appsettings.json</c>.</para>
/// </summary>
public sealed class OandaOptions
{
    public const string SectionName = "Oanda";

    /// <summary>True for the practice environment (a demo account). Selects both hosts.</summary>
    public bool Practice { get; set; } = true;

    /// <summary>REST host for the practice environment.</summary>
    public string PracticeRestBaseUrl { get; set; } = "https://api-fxpractice.oanda.com";

    /// <summary>Streaming host for the practice environment.</summary>
    public string PracticeStreamBaseUrl { get; set; } = "https://stream-fxpractice.oanda.com";

    /// <summary>REST host for the live environment.</summary>
    public string LiveRestBaseUrl { get; set; } = "https://api-fxtrade.oanda.com";

    /// <summary>Streaming host for the live environment.</summary>
    public string LiveStreamBaseUrl { get; set; } = "https://stream-fxtrade.oanda.com";

    /// <summary>The v20 account id, in the form <c>001-001-1234567-001</c>. Every pricing and candle
    /// path is scoped to an account, so nothing works without it.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Instruments to offer, in OANDA's underscore form. Empty asks the account which
    /// instruments it can trade and offers all of them.</summary>
    public string[] Instruments { get; set; } = [];

    /// <summary>Which side of the book candles are built from: <c>M</c> (mid), <c>B</c> (bid) or
    /// <c>A</c> (ask). Mid is the sane default for charting — a bid chart and an ask chart of the same
    /// market disagree by the spread and neither is "the price".</summary>
    public string CandlePrice { get; set; } = "M";

    /// <summary>Bars per history request. OANDA caps a single call at 5000.</summary>
    public int MaxCandles { get; set; } = 500;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>The REST host for the configured environment.</summary>
    public string RestBaseUrl => Practice ? PracticeRestBaseUrl : LiveRestBaseUrl;

    /// <summary>The streaming host for the configured environment.</summary>
    public string StreamBaseUrl => Practice ? PracticeStreamBaseUrl : LiveStreamBaseUrl;
}
