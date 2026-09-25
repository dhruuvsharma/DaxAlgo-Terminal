namespace TradingTerminal.Core.Configuration;

// The US and global brokers (2026-09-25). Hosts and instruments only — keys, secrets and sessions live in
// the DPAPI store and reach each client through IBrokerCredentialSource. Every client here was written
// from its broker's published API and none has run against a real account yet.

/// <summary>Charles Schwab Trader API. Section <c>CharlesSchwab</c>. Symbols are Schwab's (<c>AAPL</c>,
/// <c>$SPX</c> for an index).</summary>
public sealed class SchwabOptions : SessionBrokerOptions
{
    public const string SectionName = "CharlesSchwab";

    public SchwabOptions()
    {
        RestBaseUrl = "https://api.schwabapi.com";
        Instruments = ["SPY", "QQQ", "AAPL", "MSFT", "NVDA", "AMZN", "TSLA", "$SPX|S&P 500 index"];
        // Schwab allows 120 market-data requests a minute; live data comes over the streamer, so only
        // bars are polled.
        PollIntervalMilliseconds = 1000;
    }

    /// <summary>OAuth host, no trailing slash.</summary>
    public string AuthBaseUrl { get; set; } = "https://api.schwabapi.com/v1/oauth";

    /// <summary>The streamer's book service: <c>NASDAQ_BOOK</c> or <c>NYSE_BOOK</c>.</summary>
    public string BookService { get; set; } = "NASDAQ_BOOK";
}

/// <summary>TradeStation v3. Section <c>TradeStation</c>. Symbols are TradeStation's (<c>AAPL</c>,
/// <c>@ES</c> for a continuous future).</summary>
public sealed class TradeStationOptions : SessionBrokerOptions
{
    public const string SectionName = "TradeStation";

    public TradeStationOptions()
    {
        RestBaseUrl = "https://api.tradestation.com";
        Instruments = ["SPY", "QQQ", "AAPL", "MSFT", "NVDA", "TSLA", "@ES|E-mini S&P 500", "@NQ|E-mini Nasdaq-100"];
    }

    /// <summary>OAuth host (Auth0), no trailing slash.</summary>
    public string AuthBaseUrl { get; set; } = "https://signin.tradestation.com";

    /// <summary>What the app asks to be allowed: market data, the account, and trading (<c>Trade</c>) since the
    /// order route (2026-09-25). A session signed in before then lacks <c>Trade</c>, and TradeStation refuses
    /// its orders until the user signs in again.</summary>
    public string Scopes { get; set; } = "openid offline_access MarketData ReadAccount Trade";
}

/// <summary>tastytrade. Section <c>Tastytrade</c>. Symbols are dxFeed's (<c>AAPL</c>, <c>/ESZ26:XCME</c>).</summary>
public sealed class TastytradeOptions : SessionBrokerOptions
{
    public const string SectionName = "Tastytrade";

    public TastytradeOptions()
    {
        RestBaseUrl = "https://api.tastyworks.com";
        Instruments = ["SPY", "QQQ", "AAPL", "MSFT", "NVDA", "TSLA", "AMZN", "IWM"];
    }

    /// <summary>How long a history request waits for the feed to finish sending candles.</summary>
    public int HistoryTimeoutSeconds { get; set; } = 15;
}

/// <summary>E*TRADE. Section <c>ETrade</c>. Symbols are tickers.</summary>
public sealed class ETradeOptions : SessionBrokerOptions
{
    public const string SectionName = "ETrade";

    public ETradeOptions()
    {
        // The sandbox is https://apisb.etrade.com, with sandbox keys.
        RestBaseUrl = "https://api.etrade.com";
        Instruments = ["SPY", "QQQ", "AAPL", "MSFT", "NVDA", "TSLA", "AMZN"];
        PollIntervalMilliseconds = 1500;
    }

    /// <summary>The page a request token is authorised on.</summary>
    public string AuthorizeUrl { get; set; } = "https://us.etrade.com/e/t/etws/authorize";
}

/// <summary>Tradovate. Section <c>Tradovate</c>. Symbols are contract names (<c>ESZ6</c>); a contract rolls,
/// so this list needs editing each quarter.</summary>
public sealed class TradovateOptions : SessionBrokerOptions
{
    public const string SectionName = "Tradovate";

    public TradovateOptions()
    {
        RestBaseUrl = "https://live.tradovateapi.com/v1";
        WsBaseUrl = "wss://md.tradovateapi.com/v1/websocket";
        Instruments = ["ESZ6|E-mini S&P 500 Dec 26", "NQZ6|E-mini Nasdaq Dec 26", "MESZ6|Micro S&P Dec 26", "MNQZ6|Micro Nasdaq Dec 26", "CLX6|Crude Nov 26", "GCZ6|Gold Dec 26"];
    }

    /// <summary>The demo environment's hosts, used when the login row's environment is <c>demo</c>.</summary>
    public string DemoRestBaseUrl { get; set; } = "https://demo.tradovateapi.com/v1";

    public string DemoWsBaseUrl { get; set; } = "wss://md-demo.tradovateapi.com/v1/websocket";

    /// <summary>The name Tradovate's API registration knows this app by.</summary>
    public string AppId { get; set; } = "DaxAlgo Terminal";
}

/// <summary>Saxo Bank OpenAPI. Section <c>SaxoBank</c>. Symbols are <c>ASSETTYPE:SYMBOL</c> — Saxo's asset
/// type and its symbol (<c>FxSpot:EURUSD</c>, <c>Stock:AAPL:xnas</c>) or its numeric instrument id.</summary>
public sealed class SaxoOptions : SessionBrokerOptions
{
    public const string SectionName = "SaxoBank";

    public SaxoOptions()
    {
        // The simulation environment is https://gateway.saxobank.com/sim/openapi with sim.logonvalidation.net.
        RestBaseUrl = "https://gateway.saxobank.com/openapi";
        Instruments = ["FxSpot:EURUSD", "FxSpot:GBPUSD", "FxSpot:USDJPY", "FxSpot:XAUUSD", "Stock:AAPL:xnas", "Stock:MSFT:xnas", "CfdOnIndex:US500.I"];
        // 120 requests a minute per service group; a quote and a book on one instrument at 2 s is 60.
        PollIntervalMilliseconds = 2000;
    }

    public string AuthBaseUrl { get; set; } = "https://live.logonvalidation.net";
}

/// <summary>IG. Section <c>IgGroup</c>. Symbols are IG epics (<c>CS.D.EURUSD.CFD.IP</c>).</summary>
public sealed class IgOptions : SessionBrokerOptions
{
    public const string SectionName = "IgGroup";

    public IgOptions()
    {
        RestBaseUrl = "https://api.ig.com/gateway/deal";
        Instruments =
        [
            "CS.D.EURUSD.CFD.IP|EUR/USD", "CS.D.GBPUSD.CFD.IP|GBP/USD", "CS.D.USDJPY.CFD.IP|USD/JPY",
            "IX.D.SPTRD.DAILY.IP|US 500", "IX.D.FTSE.DAILY.IP|FTSE 100", "IX.D.DAX.DAILY.IP|Germany 40", "CS.D.USCGC.TODAY.IP|Spot Gold",
        ];
        // IG allows 60 non-trading requests a minute per app and 30 per account.
        PollIntervalMilliseconds = 2000;
    }

    public string DemoRestBaseUrl { get; set; } = "https://demo-api.ig.com/gateway/deal";

    /// <summary>The most history points one chart request may spend. IG meters history at 10,000 points a
    /// week per account, and every point a chart loads counts.</summary>
    public int HistoryMaxPoints { get; set; } = 300;
}

/// <summary>Questrade. Section <c>Questrade</c>. Symbols are Questrade's (<c>AAPL</c>, <c>RY.TO</c>).</summary>
public sealed class QuestradeOptions : SessionBrokerOptions
{
    public const string SectionName = "Questrade";

    public QuestradeOptions()
    {
        // The API host is not fixed: each token names the server to use, and the client uses that.
        RestBaseUrl = string.Empty;
        Instruments = ["SPY", "QQQ", "AAPL", "MSFT", "RY.TO|Royal Bank", "TD.TO|TD Bank", "SHOP.TO|Shopify"];
        PollIntervalMilliseconds = 1000;
    }

    /// <summary>Where refresh tokens are exchanged. The practice account uses https://practicelogin.questrade.com.</summary>
    public string AuthBaseUrl { get; set; } = "https://login.questrade.com";
}

/// <summary>Robinhood Crypto Trading API. Section <c>RobinhoodCrypto</c>. Symbols are pairs (<c>BTC-USD</c>).</summary>
public sealed class RobinhoodCryptoOptions : SessionBrokerOptions
{
    public const string SectionName = "RobinhoodCrypto";

    public RobinhoodCryptoOptions()
    {
        RestBaseUrl = "https://trading.robinhood.com";
        Instruments = ["BTC-USD", "ETH-USD", "SOL-USD", "DOGE-USD", "XRP-USD", "AVAX-USD", "LTC-USD"];
        SizeScale = 1000;
    }
}
