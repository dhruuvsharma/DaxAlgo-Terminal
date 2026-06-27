namespace TradingTerminal.Core.Brokers;

public enum BrokerKind
{
    InteractiveBrokers,
    NinjaTrader,
    CTrader,
    Alpaca,

    /// <summary>
    /// In-process synthetic / replay backend — no broker, no network. Streams either a
    /// random-walk feed (Synthetic) or recorded data from the local store (Replay) so the app
    /// can run fully offline for development. Appended last to keep the existing ordinal values
    /// stable. See <c>SimulatedBrokerClient</c> / <c>SimulatedBrokerOptions</c>.
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
}
