namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Turns local storage of the live feed on and off while the application is running.
///
/// <para>Separate from <see cref="IMarketDataStore"/> because it is a <em>policy</em> question, not a
/// storage operation: the user is deciding whether their machine keeps a copy of the market data
/// flowing through it. Implemented by the stores; a host that composes a store with no such control
/// simply never resolves this.</para>
///
/// <para>Turning it off does not stop the live feed. Quotes, trades, depth and bars keep flowing
/// through the hub and every window keeps updating — nothing is written to disk. What is lost is the
/// warm start: the depth and trade history the order book and volume footprint replay when they open,
/// and the bar cache that saves a round trip to the broker.</para>
/// </summary>
public interface ILocalMarketDataPersistence
{
    /// <summary>Whether the live feed is currently being written to disk.</summary>
    bool IsPersistingLocally { get; }

    /// <summary>
    /// Turns local storage on or off. Takes effect immediately for data arriving after the call;
    /// already-written data is untouched.
    /// </summary>
    /// <returns>
    /// What the store settled on. This can be <see langword="false"/> after asking for
    /// <see langword="true"/> — a backend that is unreachable cannot start persisting just because
    /// someone ticked a box, and saying so is better than silently pretending.
    /// </returns>
    bool SetLocalPersistence(bool enabled);
}
