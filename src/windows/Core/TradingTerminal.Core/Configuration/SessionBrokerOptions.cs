namespace TradingTerminal.Core.Configuration;

/// <summary>
/// What every broker with a sign-in step has in common: its hosts, the instruments to offer, how often
/// to poll where it has no stream, and the reconnect policy.
///
/// <para>Symbols are written the way the broker writes them, and several brokers identify a stock by a
/// number rather than a ticker. An instrument entry may carry a label after a bar —
/// <c>NSE:2885|RELIANCE</c> — which is shown in the picker and never sent to the broker.</para>
///
/// <para><b>Credentials are never here.</b> Keys, secrets and sessions live in the DPAPI store and reach
/// the client through <c>IBrokerCredentialSource</c>; <c>appsettings.json</c> is plain text in the user's
/// profile.</para>
/// </summary>
public abstract class SessionBrokerOptions
{
    /// <summary>REST host, no trailing slash.</summary>
    public string RestBaseUrl { get; set; } = string.Empty;

    /// <summary>Streaming endpoint, where the broker has one this adapter uses.</summary>
    public string WsBaseUrl { get; set; } = string.Empty;

    /// <summary>The instruments offered in the picker, in the broker's own symbol format.</summary>
    public string[] Instruments { get; set; } = [];

    /// <summary>Sizes are multiplied by this before they become the integer size fields. Shares are
    /// whole, so 1 for equities.</summary>
    public double SizeScale { get; set; } = 1.0;

    /// <summary>How often quotes and books are polled where the broker offers no stream this adapter
    /// uses. Kept above each broker's published rate limit.</summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>How often the forming bar is re-read from history for live bars.</summary>
    public int BarPollSeconds { get; set; } = 5;

    /// <summary>The redirect URL registered with the broker for the app, for browser sign-ins. The page
    /// it lands on does not have to exist: the code is read from the address bar.</summary>
    public string RedirectUri { get; set; } = "https://127.0.0.1/";

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}

/// <summary>Zerodha Kite Connect. Section <c>Zerodha</c>. Symbols are <c>EXCHANGE:TRADINGSYMBOL</c>.</summary>
public sealed class ZerodhaOptions : SessionBrokerOptions
{
    public const string SectionName = "Zerodha";

    public ZerodhaOptions()
    {
        RestBaseUrl = "https://api.kite.trade";
        WsBaseUrl = "wss://ws.kite.trade";
        Instruments = ["NSE:RELIANCE", "NSE:HDFCBANK", "NSE:INFY", "NSE:TCS", "NSE:ICICIBANK", "NSE:SBIN", "NSE:ITC", "NSE:NIFTY 50"];
        // Quote and historical endpoints allow a few requests a second.
        PollIntervalMilliseconds = 1000;
    }
}

/// <summary>Angel One SmartAPI. Section <c>AngelOne</c>. Symbols are <c>EXCHANGE:TOKEN</c>, the token being
/// Angel's scrip-master token (the NSE token for equities).</summary>
public sealed class AngelOneOptions : SessionBrokerOptions
{
    public const string SectionName = "AngelOne";

    public AngelOneOptions()
    {
        RestBaseUrl = "https://apiconnect.angelone.in";
        WsBaseUrl = "wss://smartapisocket.angelone.in/smart-stream";
        Instruments = ["NSE:2885|RELIANCE", "NSE:1333|HDFCBANK", "NSE:1594|INFY", "NSE:11536|TCS", "NSE:4963|ICICIBANK", "NSE:3045|SBIN", "NSE:1660|ITC"];
    }
}

/// <summary>Dhan. Section <c>Dhan</c>. Symbols are <c>SEGMENT:SECURITYID</c> (<c>NSE_EQ:1333</c>).</summary>
public sealed class DhanOptions : SessionBrokerOptions
{
    public const string SectionName = "Dhan";

    public DhanOptions()
    {
        RestBaseUrl = "https://api.dhan.co/v2";
        WsBaseUrl = "wss://api-feed.dhan.co";
        Instruments = ["NSE_EQ:2885|RELIANCE", "NSE_EQ:1333|HDFCBANK", "NSE_EQ:1594|INFY", "NSE_EQ:11536|TCS", "NSE_EQ:4963|ICICIBANK", "NSE_EQ:3045|SBIN", "NSE_EQ:1660|ITC"];
        // One quote request a second is Dhan's published limit; the live feed carries the rest.
        PollIntervalMilliseconds = 1500;
    }
}

/// <summary>Fyers API v3. Section <c>Fyers</c>. Symbols are Fyers' own (<c>NSE:SBIN-EQ</c>).</summary>
public sealed class FyersOptions : SessionBrokerOptions
{
    public const string SectionName = "Fyers";

    public FyersOptions()
    {
        RestBaseUrl = "https://api-t1.fyers.in";
        Instruments = ["NSE:RELIANCE-EQ", "NSE:HDFCBANK-EQ", "NSE:INFY-EQ", "NSE:TCS-EQ", "NSE:ICICIBANK-EQ", "NSE:SBIN-EQ", "NSE:ITC-EQ", "NSE:NIFTY50-INDEX"];
        // Fyers allows 10 requests a second and 200 a minute; a quote and a book poll per instrument
        // each second would spend 120 of those 200.
        PollIntervalMilliseconds = 2000;
    }
}

/// <summary>5paisa Xstream. Section <c>FivePaisa</c>. Symbols are <c>EXCH:TYPE:SCRIPCODE</c>
/// (<c>N:C:2885</c> — NSE cash, Reliance).</summary>
public sealed class FivePaisaOptions : SessionBrokerOptions
{
    public const string SectionName = "FivePaisa";

    public FivePaisaOptions()
    {
        RestBaseUrl = "https://openapi.5paisa.com";
        WsBaseUrl = "wss://openfeed.5paisa.com/feeds/api/chat";
        Instruments = ["N:C:2885|RELIANCE", "N:C:1333|HDFCBANK", "N:C:1594|INFY", "N:C:11536|TCS", "N:C:4963|ICICIBANK", "N:C:3045|SBIN", "N:C:1660|ITC"];
    }
}

/// <summary>Alice Blue ANT. Section <c>AliceBlue</c>. Symbols are <c>EXCHANGE:TOKEN</c>.</summary>
public sealed class AliceBlueOptions : SessionBrokerOptions
{
    public const string SectionName = "AliceBlue";

    public AliceBlueOptions()
    {
        RestBaseUrl = "https://ant.aliceblueonline.com/rest/AliceBlueAPIService/api";
        WsBaseUrl = "wss://ws1.aliceblueonline.com/NorenWS/";
        Instruments = ["NSE:2885|RELIANCE", "NSE:1333|HDFCBANK", "NSE:1594|INFY", "NSE:11536|TCS", "NSE:4963|ICICIBANK", "NSE:3045|SBIN", "NSE:1660|ITC", "NSE:26000|NIFTY 50"];
    }
}

/// <summary>ICICI Direct Breeze. Section <c>IciciBreeze</c>. Symbols are <c>EXCHANGE:STOCKCODE</c>, the
/// stock code being ICICI's own (<c>RELIND</c> is Reliance, <c>INFTEC</c> Infosys).</summary>
public sealed class IciciBreezeOptions : SessionBrokerOptions
{
    public const string SectionName = "IciciBreeze";

    public IciciBreezeOptions()
    {
        RestBaseUrl = "https://api.icicidirect.com/breezeapi/api/v1";
        Instruments = ["NSE:RELIND|RELIANCE", "NSE:HDFBAN|HDFCBANK", "NSE:INFTEC|INFY", "NSE:TCS|TCS", "NSE:ICIBAN|ICICIBANK", "NSE:STABAN|SBIN", "NSE:ITC|ITC"];
        // Breeze allows 100 calls a minute.
        PollIntervalMilliseconds = 2000;
    }

    /// <summary>Breeze serves history from a second host (its "v2" charts API).</summary>
    public string HistoryBaseUrl { get; set; } = "https://breezeapi.icicidirect.com/api/v2";
}
