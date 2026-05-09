using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Singleton seam between the broker-picker UI and the market-data layer. Holds the
/// currently active <see cref="IBrokerClient"/>; the login screen flips it before
/// triggering the first connect, and everything downstream (repository, connection
/// manager, view-models) reads through <see cref="Active"/>.
/// </summary>
public interface IBrokerSelector
{
    BrokerKind ActiveKind { get; }

    IBrokerClient Active { get; }

    BrokerConnectionMode ActiveMode { get; }

    /// <summary>Raised on the calling thread whenever <see cref="ActiveKind"/> changes.</summary>
    event EventHandler? ActiveChanged;

    void SetActive(BrokerKind kind);
}
