using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.CTrader;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.ExecutionUi.Tests;

internal sealed class TestBrokerLoginFormFactory(params IBrokerLoginForm[] forms) : IBrokerLoginFormFactory
{
    private readonly IReadOnlyDictionary<BrokerKind, IBrokerLoginForm> _forms =
        forms.ToDictionary(form => form.Broker);

    public IReadOnlyList<IBrokerLoginForm> All { get; } = Array.AsReadOnly(forms);

    public IBrokerLoginForm Get(BrokerKind kind) =>
        _forms.TryGetValue(kind, out var form)
            ? form
            : throw new InvalidOperationException($"No test Login form exists for {kind}.");

    internal static TestBrokerLoginFormFactory Alpaca()
    {
        var selector = new TestBrokerSelector(BrokerKind.Alpaca);
        var store = new CredentialStore(NullLogger<CredentialStore>.Instance);
        return new TestBrokerLoginFormFactory(new AlpacaLoginFormViewModel(
            Options.Create(new AlpacaOptions()),
            store,
            selector,
            NullLogger<AlpacaLoginFormViewModel>.Instance));
    }

    internal static TestBrokerLoginFormFactory InteractiveBrokers()
    {
        var selector = new TestBrokerSelector(BrokerKind.InteractiveBrokers);
        var store = new CredentialStore(NullLogger<CredentialStore>.Instance);
        return new TestBrokerLoginFormFactory(new IbLoginFormViewModel(
            Options.Create(new InteractiveBrokersOptions()),
            store,
            selector,
            NullLogger<IbLoginFormViewModel>.Instance));
    }

    internal static TestBrokerLoginFormFactory CTrader()
    {
        var selector = new TestBrokerSelector(BrokerKind.CTrader);
        var store = new CredentialStore(NullLogger<CredentialStore>.Instance);
        return new TestBrokerLoginFormFactory(new CTraderLoginFormViewModel(
            Options.Create(new CTraderOptions()),
            store,
            new EmptyCTraderAccountDiscovery(),
            selector,
            NullLogger<CTraderLoginFormViewModel>.Instance));
    }

    private sealed class EmptyCTraderAccountDiscovery : ICTraderAccountDiscovery
    {
        public Task<IReadOnlyList<CTraderDiscoveredAccount>> DiscoverAsync(
            string host,
            int port,
            string clientId,
            string clientSecret,
            string accessToken,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CTraderDiscoveredAccount>>(Array.Empty<CTraderDiscoveredAccount>());
    }

    private sealed class TestBrokerSelector(params BrokerKind[] available) : IBrokerSelector
    {
        private readonly IReadOnlyList<BrokerKind> _available = Array.AsReadOnly(available);

        public IReadOnlyList<BrokerKind> AvailableKinds => _available;

        public bool IsAvailable(BrokerKind kind) => _available.Contains(kind);

        public IReadOnlyList<BrokerKind> Connected => Array.Empty<BrokerKind>();

        public bool IsConnected(BrokerKind kind) => false;

        public IBrokerClient Get(BrokerKind kind) => throw new NotSupportedException();

        public BrokerConnectionMode ModeOf(BrokerKind kind) =>
            new(kind, IsLive: false, "Test", "Test Login-form selector.");

        public IObservable<ConnectionState> StateOf(BrokerKind kind) =>
            new ConstantObservable<ConnectionState>(ConnectionState.Disconnected);

        public ConnectionState CurrentStateOf(BrokerKind kind) => ConnectionState.Disconnected;

        public event EventHandler<BrokerStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ConstantObservable<T>(T value) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            observer.OnNext(value);
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}

internal sealed class TrackingBrokerLoginForm(BrokerKind broker) : IBrokerLoginForm, IDisposable
{
    public BrokerKind Broker { get; } = broker;

    public string DisplayName => Broker.ToString();

    public bool CanSubmit => true;

    public ConnectionState CurrentState => ConnectionState.Disconnected;

    public bool IsConnecting => false;

    public string? ErrorMessage => null;

    internal bool IsDisposed { get; private set; }

    public void ApplyToOptions() => throw new InvalidOperationException("Execution must not apply Login options.");

    public string GetSessionAccountLabel() => string.Empty;

    public string GetTimeoutErrorMessage() => string.Empty;

    public string GetFailureMessage() => string.Empty;

    public void Load() => throw new InvalidOperationException("Execution must not load persisted Login credentials.");

    public void Save() => throw new InvalidOperationException("Execution must not save Login credentials.");

    public void Dispose() => IsDisposed = true;
}
