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
}
