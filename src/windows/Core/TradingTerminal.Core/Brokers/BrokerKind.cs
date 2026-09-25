namespace TradingTerminal.Core.Brokers;

public enum BrokerKind
{
    InteractiveBrokers,
    NinjaTrader,
    CTrader,
    Alpaca,

    /// <summary>
    /// <b>Not a connectable broker.</b> A PROVENANCE TAG for data that did not come from a live
    /// venue — backtest feeds, synthetic tapes, CSV/Parquet replays — so a row in the store can
    /// always answer "where did this come from".
    ///
    /// <para>The in-process synthetic/replay broker this used to name was removed on 2026-08-16:
    /// there is no <c>SimulatedBrokerClient</c>, no options section, no registration, and it does
    /// not appear in <see cref="Configuration.BrokerEditionPolicy"/>, so it cannot be selected or
    /// connected. The member itself stays because <see cref="BrokerKind"/> is persisted <b>by
    /// ordinal</b> into SQLite, Postgres, QuestDB, Parquet and archive bundles — deleting it would
    /// shift every later value and silently re-label users' existing history.</para>
    /// </summary>
    Simulated,

    /// <summary>
    /// Binance public market data — real, live crypto bars / L1 / L2 / trades over the exchange's
    /// public WebSocket + REST endpoints, with no API key and no account. Data-only (this build
    /// places no orders anyway). Lets anyone run the terminal against a real feed with zero
    /// credentials. Appended last to keep existing ordinal values stable.
    /// See <c>RealBinanceClient</c> / <c>BinanceOptions</c>.
    /// </summary>
    Binance,

    /// <summary>
    /// Ironbeam futures (FCM) — REST + WebSocket API v2 against demo.ironbeamapi.com /
    /// live.ironbeamapi.com. JWT auth (POST /v2/auth with username + API key), market data via a
    /// server-created stream (GET /stream/create → wss://{host}/v2/stream/{streamId}?token=...).
    /// Supplies L1 quotes, L2 depth, and a real trade tape.
    /// See <c>RealIronBeamClient</c> / <c>IronBeamOptions</c>.
    /// </summary>
    IronBeam,

    /// <summary>
    /// London Strategic Edge — free multi-asset market data (stocks, FX, crypto, commodities,
    /// indices, ETFs) over a single WebSocket (wss://data-ws.londonstrategicedge.com) plus a
    /// PostgREST-style REST history API (api.londonstrategicedge.com/iso). API-key auth, data-only
    /// (no order path exists at the provider at all). Supplies L1 ticks and historical OHLCV; no
    /// depth, and the trade tape is not wired until the tick stream is verified to carry true
    /// prints. Appended last to keep existing ordinal values stable.
    /// See <c>RealLondonStrategicEdgeClient</c> / <c>LondonStrategicEdgeOptions</c>.
    /// </summary>
    LondonStrategicEdge,

    /// <summary>
    /// Upstox — Indian-market broker (NSE/BSE equities, F&amp;O, commodities) over the Upstox API v2/v3
    /// (REST + WebSocket, no SDK). OAuth2 auth (authorization-code → access token, expires ~03:30 IST
    /// daily). Live ticks + 5-level depth stream over the V3 protobuf market-data feed
    /// (<c>wss://…/v3/feed/market-data-feed</c>); historical candles + the instrument master come over
    /// REST. No real trade tape (the feed carries LTP + book, not per-print flow) — strategies fall
    /// back to the synthetic L1 tick rule. Appended last to keep existing ordinal values stable.
    /// See <c>RealUpstoxClient</c> / <c>UpstoxOptions</c>.
    /// </summary>
    Upstox,

    /// <summary>
    /// Coinbase public market data — real, live crypto bars / L1 / L2 / trades over the Advanced
    /// Trade WebSocket (wss://advanced-trade-ws.coinbase.com: level2 / ticker / market_trades) plus
    /// REST candles (api.exchange.coinbase.com). No API key, no account. Data-only. Appended last to
    /// keep existing ordinal values stable. See <c>RealCoinbaseClient</c> / <c>CoinbaseOptions</c>.
    /// </summary>
    Coinbase,

    /// <summary>
    /// Bybit public market data — real, live crypto bars / L1 / L2 / trades over the v5 public
    /// WebSocket (wss://stream.bybit.com/v5/public/spot: orderbook / tickers / publicTrade / kline)
    /// plus REST kline. No API key, no account. Data-only. Appended last to keep existing ordinal
    /// values stable. See <c>RealBybitClient</c> / <c>BybitOptions</c>.
    /// </summary>
    Bybit,

    /// <summary>
    /// Kraken public market data — real, live crypto bars / L1 / L2 / trades over the WebSocket v2
    /// (wss://ws.kraken.com/v2: book / ticker / trade / ohlc) plus REST OHLC. No API key, no account.
    /// Data-only. Appended last to keep existing ordinal values stable.
    /// See <c>RealKrakenClient</c> / <c>KrakenOptions</c>.
    /// </summary>
    Kraken,

    /// <summary>
    /// OKX public market data — real, live crypto bars / L1 / L2 / trades over the v5 public
    /// WebSocket (wss://ws.okx.com:8443/ws/v5/public: books5 / tickers / trades / candle) plus REST
    /// candles. No API key, no account. Data-only. Appended last to keep existing ordinal values
    /// stable. See <c>RealOkxClient</c> / <c>OkxOptions</c>.
    /// </summary>
    Okx,

    /// <summary>OANDA v20 — forex and CFD market data. Appended, never inserted: this enum travels on
    /// every quote and bar as provenance, and renumbering it would silently relabel stored data.</summary>
    Oanda,

    /// <summary>Deribit — crypto options and perpetuals. Appended, never inserted.</summary>
    Deribit,

    /// <summary>Hyperliquid — the perpetuals DEX. Appended, never inserted.</summary>
    Hyperliquid,

    /// <summary>Tradier — US equities and options. Appended, never inserted.</summary>
    Tradier,

    // ── Public crypto venues, added together (2026-09-25). Appended, never inserted. ──────────────

    /// <summary>Bitget — spot, public v2 WebSocket. See <c>RealBitgetClient</c>.</summary>
    Bitget,

    /// <summary>KuCoin — spot, public WebSocket behind a per-connection token. See <c>RealKuCoinClient</c>.</summary>
    KuCoin,

    /// <summary>Gate.io — spot, public v4 WebSocket. See <c>RealGateIoClient</c>.</summary>
    GateIo,

    /// <summary>Gemini — spot, public v2 market-data WebSocket. See <c>RealGeminiClient</c>.</summary>
    Gemini,

    /// <summary>Crypto.com Exchange — spot, public v1 market WebSocket. See <c>RealCryptoComClient</c>.</summary>
    CryptoCom,

    /// <summary>Upbit — Korean won markets. See <c>RealUpbitClient</c>.</summary>
    Upbit,

    /// <summary>Bithumb — Korean won markets, Upbit-shaped API. See <c>RealBithumbClient</c>.</summary>
    Bithumb,

    /// <summary>Bitfinex — spot, public v2 WebSocket. See <c>RealBitfinexClient</c>.</summary>
    Bitfinex,

    /// <summary>Bitstamp — spot, public WebSocket. See <c>RealBitstampClient</c>.</summary>
    Bitstamp,

    /// <summary>Bitvavo — euro markets, public v2 WebSocket. See <c>RealBitvavoClient</c>.</summary>
    Bitvavo,

    /// <summary>HTX (formerly Huobi) — spot, gzip WebSocket. See <c>RealHtxClient</c>.</summary>
    Htx,

    /// <summary>MEXC — spot, protobuf WebSocket. See <c>RealMexcClient</c>.</summary>
    Mexc,

    // ── Indian brokers, added together (2026-09-25). Appended, never inserted. ─────────────────────

    /// <summary>Zerodha Kite Connect — REST + binary ticker. See <c>RealZerodhaClient</c>.</summary>
    Zerodha,

    /// <summary>Angel One SmartAPI — REST + binary SmartStream. See <c>RealAngelOneClient</c>.</summary>
    AngelOne,

    /// <summary>Dhan (DhanHQ v2) — REST + binary live feed. See <c>RealDhanClient</c>.</summary>
    Dhan,

    /// <summary>Fyers API v3 — REST. See <c>RealFyersClient</c>.</summary>
    Fyers,

    /// <summary>5paisa Xstream — REST + JSON feed. See <c>RealFivePaisaClient</c>.</summary>
    FivePaisa,

    /// <summary>Alice Blue ANT — REST + Noren feed. See <c>RealAliceBlueClient</c>.</summary>
    AliceBlue,

    /// <summary>ICICI Direct Breeze — REST. See <c>RealIciciBreezeClient</c>.</summary>
    IciciBreeze,

    // ── US and global brokers, added together (2026-09-25). Appended, never inserted. ──────────────

    /// <summary>Charles Schwab Trader API. See <c>RealSchwabClient</c>.</summary>
    CharlesSchwab,

    /// <summary>TradeStation v3 — REST + HTTP streaming. See <c>RealTradeStationClient</c>.</summary>
    TradeStation,

    /// <summary>tastytrade — OAuth2 + DXLink. See <c>RealTastytradeClient</c>.</summary>
    Tastytrade,

    /// <summary>E*TRADE — OAuth 1.0a REST. See <c>RealETradeClient</c>.</summary>
    ETrade,

    /// <summary>Tradovate — futures, WebSocket market data. See <c>RealTradovateClient</c>.</summary>
    Tradovate,

    /// <summary>Saxo Bank OpenAPI. See <c>RealSaxoClient</c>.</summary>
    SaxoBank,

    /// <summary>IG — REST. See <c>RealIgClient</c>.</summary>
    IgGroup,

    /// <summary>Questrade — REST. See <c>RealQuestradeClient</c>.</summary>
    Questrade,

    /// <summary>Robinhood Crypto Trading API. See <c>RealRobinhoodCryptoClient</c>.</summary>
    RobinhoodCrypto,
}
