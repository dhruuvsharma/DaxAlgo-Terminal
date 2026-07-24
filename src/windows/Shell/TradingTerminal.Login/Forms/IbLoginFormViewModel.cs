using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

// Note: NOT partial / no [ObservableProperty]. WPF's MarkupCompilePass1 runs in a temporary
// _wpftmp.csproj that doesn't always cooperate with source generators on partial classes used
// in <DataTemplate DataType="{x:Type ...}">. Using manual SetProperty avoids the issue.
public sealed class IbLoginFormViewModel : BrokerLoginFormBase
{
    private readonly InteractiveBrokersOptions _options;
    private readonly CredentialStore _credentialStore;
    private readonly ServiceDependencyViewModel _prerequisite;

    public IbLoginFormViewModel(
        IOptions<InteractiveBrokersOptions> options,
        CredentialStore credentialStore,
        IBrokerSelector selector,
        ILogger<IbLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;

        // The desktop app is IB's transport, so its status belongs on this form rather than in the
        // shell's shared services panel. Probes whatever Host/Port this form will actually dial.
        _prerequisite = new ServiceDependencyViewModel(
            name: "TWS / IB Gateway",
            purpose: "Interactive Brokers data arrives through the desktop app — it has to be running and signed in before Connect works.",
            requirement: "Required",
            howTo: "Launch TWS or IB Gateway and log in, then enable API access: Config → API → Settings → " +
                   "“Enable ActiveX and Socket Clients”. Ports: TWS paper 7497 / live 7496, Gateway paper 4002 / live 4001. " +
                   "With 2FA on, complete the prompt in TWS first — the API socket has no separate 2FA step.",
            startCommand: null,
            probe: ct => ServiceDependencyViewModel.TcpOpenAsync(Host, new[] { Port }, ct));

        AccountTypes = new[] { "Paper", "Live" };
        MarketDataTypes = new[]
        {
            new MarketDataTypeOption(1, "Live (requires subscription)"),
            new MarketDataTypeOption(3, "Delayed (free, ~15 min lag)"),
            new MarketDataTypeOption(4, "Delayed-Frozen (free, last known)"),
        };
        _selectedMarketDataType = MarketDataTypes[0];
    }

    public override BrokerKind Broker => BrokerKind.InteractiveBrokers;
    public override string DisplayName => "Interactive Brokers";

    /// <summary>TWS / IB Gateway must be up — surfaced inside this row (see the base class).</summary>
    public override ServiceDependencyViewModel? Prerequisite => _prerequisite;

    public IReadOnlyList<string> AccountTypes { get; }
    public IReadOnlyList<MarketDataTypeOption> MarketDataTypes { get; }

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _password = string.Empty;
    public string Password { get => _password; set => SetProperty(ref _password, value); }

    private string _host = "127.0.0.1";
    public string Host { get => _host; set { if (SetProperty(ref _host, value)) RaiseCanSubmit(); } }

    private int _port = 7497;
    public int Port { get => _port; set { if (SetProperty(ref _port, value)) RaiseCanSubmit(); } }

    private int _clientId = 1;
    public int ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }

    private string _accountType = "Paper";
    public string AccountType { get => _accountType; set => SetProperty(ref _accountType, value); }

    private MarketDataTypeOption? _selectedMarketDataType;
    public MarketDataTypeOption? SelectedMarketDataType
    {
        get => _selectedMarketDataType;
        set => SetProperty(ref _selectedMarketDataType, value);
    }

    private bool _rememberPassword;
    public bool RememberPassword { get => _rememberPassword; set => SetProperty(ref _rememberPassword, value); }

    public override bool CanSubmit => !string.IsNullOrWhiteSpace(Host) && Port > 0;

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void ApplyToOptions()
    {
        _options.Host = Host;
        _options.Port = Port;
        _options.ClientId = ClientId;
        _options.AccountType = AccountType;
        _options.MarketDataType = SelectedMarketDataType?.Value ?? 1;
    }

    public override string GetSessionAccountLabel() => AccountType;

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s. Verify TWS / IB Gateway is running on {Host}:{Port} and that API access is enabled. " +
        "If you have 2FA enabled, complete the 2FA prompt in TWS before signing in here.";

    public override string GetFailureMessage() =>
        "TWS reported a connection failure. Common causes: API access not enabled in TWS Global Config, " +
        "wrong port (TWS Paper=7497, TWS Live=7496, Gateway Paper=4002, Gateway Live=4001), " +
        "or client id already in use by another connection.";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        Host = stored.Host;
        Port = stored.Port;
        ClientId = stored.ClientId;
        AccountType = stored.AccountType;
        var storedType = stored.MarketDataType is 1 or 3 or 4 ? stored.MarketDataType : 1;
        SelectedMarketDataType = MarketDataTypes.First(o => o.Value == storedType);
        RememberPassword = stored.RememberPassword;
        Password = stored.Password ?? string.Empty;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.InteractiveBrokers;
        stored.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        stored.Host = Host;
        stored.Port = Port;
        stored.ClientId = ClientId;
        stored.AccountType = AccountType;
        stored.MarketDataType = SelectedMarketDataType?.Value ?? 1;
        stored.RememberPassword = RememberPassword;
        stored.Password = (RememberPassword && !string.IsNullOrEmpty(Password)) ? Password : null;
        _credentialStore.Save(stored);
    }
}

public sealed record MarketDataTypeOption(int Value, string DisplayName);
