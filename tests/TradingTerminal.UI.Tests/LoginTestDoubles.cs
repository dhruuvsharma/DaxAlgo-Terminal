using System.Reactive.Subjects;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// A broker selector with a real per-broker state stream, for driving login rows through Connect.
///
/// <para>Replays the current state to a new subscriber, as the real selector's connection managers do —
/// a login row subscribing after its broker connected has to see "Connected", not wait for a change.</para>
/// </summary>
internal sealed class LiveStateSelector : IBrokerSelector
{
    private readonly Dictionary<BrokerKind, BehaviorSubject<ConnectionState>> _states = new();

    public LiveStateSelector(params BrokerKind[] available)
    {
        foreach (var kind in available)
            _states[kind] = new BehaviorSubject<ConnectionState>(ConnectionState.Disconnected);
    }

    /// <summary>How many times a connect was started, per broker.</summary>
    public Dictionary<BrokerKind, int> ConnectCalls { get; } = new();

    /// <summary>Runs at the moment a connect starts, before the broker reports Connected — where a
    /// real client reads its credentials.</summary>
    public Action<BrokerKind>? OnConnect { get; set; }

    public void Set(BrokerKind kind, ConnectionState state) => _states[kind].OnNext(state);

    public IReadOnlyList<BrokerKind> AvailableKinds => [.. _states.Keys];

    public IReadOnlyList<BrokerKind> Connected =>
        [.. _states.Where(entry => entry.Value.Value == ConnectionState.Connected).Select(entry => entry.Key)];

    public bool IsAvailable(BrokerKind kind) => _states.ContainsKey(kind);

    public bool IsConnected(BrokerKind kind) => CurrentStateOf(kind) == ConnectionState.Connected;

    public event EventHandler<BrokerStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public IBrokerClient Get(BrokerKind kind) => throw new NotSupportedException();

    public BrokerConnectionMode ModeOf(BrokerKind kind) => throw new NotSupportedException();

    public IObservable<ConnectionState> StateOf(BrokerKind kind) => _states[kind];

    public ConnectionState CurrentStateOf(BrokerKind kind) => _states[kind].Value;

    public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default)
    {
        ConnectCalls[kind] = ConnectCalls.GetValueOrDefault(kind) + 1;
        OnConnect?.Invoke(kind);
        Set(kind, ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default)
    {
        Set(kind, ConnectionState.Disconnected);
        return Task.CompletedTask;
    }
}

/// <summary>A venue that refuses every key, as a probe answering 401 would.</summary>
internal sealed class RefusingVerifier : IBrokerCredentialVerifier
{
    public bool CanVerify(BrokerKind broker) => true;

    public Task<CredentialVerification> VerifyAsync(
        BrokerKind broker, BrokerCredential credential, CancellationToken ct = default) =>
        Task.FromResult(CredentialVerification.Refused("Invalid API-key."));
}
