using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Multi-session broker registry. Each broker has its own independent connection lifecycle
/// (own credentials, own reconnect loop, own state stream). The login screen connects each
/// broker the user wants to use; downstream code routes per request by <see cref="BrokerKind"/>
/// (typically derived from the instrument's source broker).
///
/// There is no "active" broker anymore — every operation explicitly names which broker it
/// wants. Connect-anywhere code (order routing while no instrument is in hand) picks
/// <see cref="Connected"/>'s first entry as a fallback.
/// </summary>
public interface IBrokerSelector
{
    /// <summary>Brokers that have a registered <see cref="IBrokerClient"/> in DI (SDK was present at build time).</summary>
    IReadOnlyList<BrokerKind> AvailableKinds { get; }

    /// <summary>True when a real client for <paramref name="kind"/> is registered.</summary>
    bool IsAvailable(BrokerKind kind);

    /// <summary>Snapshot of brokers currently in <see cref="ConnectionState.Connected"/>.</summary>
    IReadOnlyList<BrokerKind> Connected { get; }

    bool IsConnected(BrokerKind kind);

    /// <summary>Per-broker client lookup. Throws if the broker isn't <see cref="IsAvailable"/>.</summary>
    IBrokerClient Get(BrokerKind kind);

    BrokerConnectionMode ModeOf(BrokerKind kind);

    /// <summary>Per-broker hot state stream. Always replays the current value to new subscribers.</summary>
    IObservable<ConnectionState> StateOf(BrokerKind kind);

    /// <summary>Synchronous current state for <paramref name="kind"/>.</summary>
    ConnectionState CurrentStateOf(BrokerKind kind);

    /// <summary>Fires every time a broker's <see cref="ConnectionState"/> changes.</summary>
    event EventHandler<BrokerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Start (or re-start) the reconnect loop for <paramref name="kind"/>. Idempotent — returns
    /// quickly if the broker is already in a Connecting/Connected state. The actual connect
    /// attempt and any backoff happens in the background; callers observe <see cref="StateOf"/>
    /// or <see cref="StateChanged"/> for completion.
    /// </summary>
    Task ConnectAsync(BrokerKind kind, CancellationToken ct = default);

    /// <summary>Stop the reconnect loop and disconnect <paramref name="kind"/>.</summary>
    Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default);
}

public sealed record BrokerStateChangedEventArgs(BrokerKind Kind, ConnectionState State);
