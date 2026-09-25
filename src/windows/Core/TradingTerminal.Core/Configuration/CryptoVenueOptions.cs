namespace TradingTerminal.Core.Configuration;

/// <summary>
/// What every public crypto venue's options have in common: where to connect, which instruments to
/// offer, how to scale fractional sizes, and the reconnect policy.
///
/// <para>A base rather than twelve copies. The older venues (Binance, Coinbase, Bybit, Kraken, OKX) each
/// carry their own class with the same eight properties spelled out; the venues added on 2026-09-25
/// derive from this one and set only what differs — the hosts and the default symbols.</para>
///
/// <para>Symbols are written the way the venue writes them (<c>BTC-USDT</c> at KuCoin, <c>BTC_USDT</c>
/// at Gate.io, <c>KRW-BTC</c> at Upbit). Translating one house style into twelve would put a mapping
/// layer between the user and the venue's own documentation for no gain.</para>
/// </summary>
public abstract class CryptoVenueOptions
{
    /// <summary>REST host, no trailing slash. Used for the connect check and bar history.</summary>
    public string RestBaseUrl { get; set; } = string.Empty;

    /// <summary>Public WebSocket endpoint.</summary>
    public string WsBaseUrl { get; set; } = string.Empty;

    /// <summary>The instruments offered in the picker, in the venue's own symbol format.</summary>
    public string[] Instruments { get; set; } = [];

    /// <summary>Fractional sizes are multiplied by this before they become the integer size fields —
    /// 0.001 BTC at a scale of 1000 is a size of 1.</summary>
    public double SizeScale { get; set; } = 1000.0;

    /// <summary>Book depth to subscribe to, where the venue lets it be chosen.</summary>
    public int DepthLevels { get; set; } = 20;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;

    public int ReconnectMaxDelaySeconds { get; set; } = 30;

    /// <summary>Filled at runtime by the keyed login row from the DPAPI store. Empty means keyless,
    /// which is the normal state — never populated from <c>appsettings.json</c>.</summary>
    public CryptoApiCredentials Credentials { get; set; } = new();
}

/// <summary>Bitget spot. Section <c>Bitget</c>.</summary>
public sealed class BitgetOptions : CryptoVenueOptions
{
    public const string SectionName = "Bitget";

    public BitgetOptions()
    {
        RestBaseUrl = "https://api.bitget.com";
        WsBaseUrl = "wss://ws.bitget.com/v2/ws/public";
        Instruments = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT", "ADAUSDT", "LINKUSDT", "LTCUSDT", "BGBUSDT"];
        DepthLevels = 15;
    }
}

/// <summary>KuCoin spot. Section <c>KuCoin</c>. The socket URL is issued per connection by
/// <c>POST /api/v1/bullet-public</c>, so <see cref="CryptoVenueOptions.WsBaseUrl"/> is only the fallback.</summary>
public sealed class KuCoinOptions : CryptoVenueOptions
{
    public const string SectionName = "KuCoin";

    public KuCoinOptions()
    {
        RestBaseUrl = "https://api.kucoin.com";
        WsBaseUrl = "wss://ws-api-spot.kucoin.com/";
        Instruments = ["BTC-USDT", "ETH-USDT", "SOL-USDT", "XRP-USDT", "DOGE-USDT", "ADA-USDT", "LINK-USDT", "LTC-USDT", "KCS-USDT"];
        DepthLevels = 50;
    }
}

/// <summary>Gate.io spot. Section <c>GateIo</c>.</summary>
public sealed class GateIoOptions : CryptoVenueOptions
{
    public const string SectionName = "GateIo";

    public GateIoOptions()
    {
        RestBaseUrl = "https://api.gateio.ws";
        WsBaseUrl = "wss://api.gateio.ws/ws/v4/";
        Instruments = ["BTC_USDT", "ETH_USDT", "SOL_USDT", "XRP_USDT", "DOGE_USDT", "ADA_USDT", "LINK_USDT", "LTC_USDT", "GT_USDT"];
        DepthLevels = 20;
    }
}

/// <summary>Gemini spot. Section <c>Gemini</c>.</summary>
public sealed class GeminiOptions : CryptoVenueOptions
{
    public const string SectionName = "Gemini";

    public GeminiOptions()
    {
        RestBaseUrl = "https://api.gemini.com";
        WsBaseUrl = "wss://api.gemini.com/v2/marketdata";
        Instruments = ["BTCUSD", "ETHUSD", "SOLUSD", "XRPUSD", "DOGEUSD", "LINKUSD", "LTCUSD", "BTCUSDT", "ETHBTC"];
    }
}

/// <summary>Crypto.com Exchange spot. Section <c>CryptoCom</c>.</summary>
public sealed class CryptoComOptions : CryptoVenueOptions
{
    public const string SectionName = "CryptoCom";

    public CryptoComOptions()
    {
        RestBaseUrl = "https://api.crypto.com/exchange/v1";
        WsBaseUrl = "wss://stream.crypto.com/exchange/v1/market";
        Instruments = ["BTC_USD", "ETH_USD", "SOL_USD", "XRP_USD", "DOGE_USD", "ADA_USD", "LINK_USD", "BTC_USDT", "CRO_USD"];
        DepthLevels = 50;
    }
}

/// <summary>Upbit, won markets. Section <c>Upbit</c>.</summary>
public sealed class UpbitOptions : CryptoVenueOptions
{
    public const string SectionName = "Upbit";

    public UpbitOptions()
    {
        RestBaseUrl = "https://api.upbit.com";
        WsBaseUrl = "wss://api.upbit.com/websocket/v1";
        Instruments = ["KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL", "KRW-DOGE", "KRW-ADA", "KRW-LINK", "USDT-BTC", "BTC-ETH"];
        DepthLevels = 15;
    }
}

/// <summary>Bithumb, won markets. Section <c>Bithumb</c>.</summary>
public sealed class BithumbOptions : CryptoVenueOptions
{
    public const string SectionName = "Bithumb";

    public BithumbOptions()
    {
        RestBaseUrl = "https://api.bithumb.com";
        WsBaseUrl = "wss://ws-api.bithumb.com/websocket/v1";
        Instruments = ["KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL", "KRW-DOGE", "KRW-ADA", "KRW-LINK", "KRW-USDT"];
        DepthLevels = 15;
    }
}

/// <summary>Bitfinex spot. Section <c>Bitfinex</c>. Pairs are written as Bitfinex lists them, without the
/// <c>t</c> prefix the socket wants — names longer than three letters carry a colon (<c>DOGE:USD</c>).</summary>
public sealed class BitfinexOptions : CryptoVenueOptions
{
    public const string SectionName = "Bitfinex";

    public BitfinexOptions()
    {
        RestBaseUrl = "https://api-pub.bitfinex.com";
        WsBaseUrl = "wss://api-pub.bitfinex.com/ws/2";
        Instruments = ["BTCUSD", "ETHUSD", "SOLUSD", "XRPUSD", "LTCUSD", "BTCUST", "ETHBTC", "DOGE:USD", "LINK:USD"];
        DepthLevels = 25;
    }
}

/// <summary>Bitstamp spot. Section <c>Bitstamp</c>. Pairs are lower-case, as its channel names are.</summary>
public sealed class BitstampOptions : CryptoVenueOptions
{
    public const string SectionName = "Bitstamp";

    public BitstampOptions()
    {
        RestBaseUrl = "https://www.bitstamp.net";
        WsBaseUrl = "wss://ws.bitstamp.net";
        Instruments = ["btcusd", "ethusd", "solusd", "xrpusd", "ltcusd", "linkusd", "btceur", "etheur", "ethbtc"];
    }
}

/// <summary>Bitvavo, euro markets. Section <c>Bitvavo</c>.</summary>
public sealed class BitvavoOptions : CryptoVenueOptions
{
    public const string SectionName = "Bitvavo";

    public BitvavoOptions()
    {
        RestBaseUrl = "https://api.bitvavo.com";
        WsBaseUrl = "wss://ws.bitvavo.com/v2/";
        Instruments = ["BTC-EUR", "ETH-EUR", "SOL-EUR", "XRP-EUR", "DOGE-EUR", "ADA-EUR", "LINK-EUR", "LTC-EUR", "USDC-EUR"];
        DepthLevels = 50;
    }
}

/// <summary>HTX (formerly Huobi) spot. Section <c>Htx</c>. Symbols are lower-case, as its channel names are.</summary>
public sealed class HtxOptions : CryptoVenueOptions
{
    public const string SectionName = "Htx";

    public HtxOptions()
    {
        RestBaseUrl = "https://api.huobi.pro";
        WsBaseUrl = "wss://api.huobi.pro/ws";
        Instruments = ["btcusdt", "ethusdt", "solusdt", "xrpusdt", "dogeusdt", "adausdt", "linkusdt", "ltcusdt", "trxusdt"];
    }
}

/// <summary>MEXC spot. Section <c>Mexc</c>.</summary>
public sealed class MexcOptions : CryptoVenueOptions
{
    public const string SectionName = "Mexc";

    public MexcOptions()
    {
        RestBaseUrl = "https://api.mexc.com";
        WsBaseUrl = "wss://wbs-api.mexc.com/ws";
        Instruments = ["BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "DOGEUSDT", "ADAUSDT", "LINKUSDT", "LTCUSDT", "MXUSDT"];
    }
}
