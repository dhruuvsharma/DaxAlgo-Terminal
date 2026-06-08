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
}
