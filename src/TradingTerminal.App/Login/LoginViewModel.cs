using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

public sealed partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly InteractiveBrokersOptions _ibOptions;
    private readonly NinjaTraderOptions _ntOptions;
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _brokerSelector;
    private readonly CredentialStore _credentialStore;
    private readonly IEnumerable<BrokerConnectionMode> _allModes;
    private readonly SessionContext _session;
    private readonly ILogger<LoginViewModel> _logger;
    private readonly IDisposable _stateSub;

    public LoginViewModel(
        IOptions<InteractiveBrokersOptions> ibOptions,
        IOptions<NinjaTraderOptions> ntOptions,
        IMarketDataRepository repository,
        IBrokerSelector brokerSelector,
        CredentialStore credentialStore,
        IEnumerable<BrokerConnectionMode> allModes,
        SessionContext session,
        ILogger<LoginViewModel> logger)
    {
        _ibOptions = ibOptions.Value;
        _ntOptions = ntOptions.Value;
        _repository = repository;
        _brokerSelector = brokerSelector;
        _credentialStore = credentialStore;
        _allModes = allModes;
        _session = session;
        _logger = logger;

        AccountTypes = new[] { "Paper", "Live" };
        MarketDataTypes = new[]
        {
            new MarketDataTypeOption(1, "Live (requires subscription)"),
            new MarketDataTypeOption(3, "Delayed (free, ~15 min lag)"),
            new MarketDataTypeOption(4, "Delayed-Frozen (free, last known)"),
        };

        var stored = _credentialStore.Load();
        SelectedBroker = stored.SelectedBroker;
        Username = stored.Username ?? string.Empty;
        Host = stored.Host;
        Port = stored.Port;
        ClientId = stored.ClientId;
        AccountType = stored.AccountType;
        var storedType = stored.MarketDataType is 1 or 3 or 4 ? stored.MarketDataType : 1;
        SelectedMarketDataType = MarketDataTypes.First(o => o.Value == storedType);
        RememberPassword = stored.RememberPassword;
        Password = stored.Password ?? string.Empty;

        NinjaAccountName = string.IsNullOrWhiteSpace(stored.NinjaAccountName) ? "Sim101" : stored.NinjaAccountName;
        NinjaDllPath = stored.NinjaDllPath;
        NinjaFuturesContractMonth = stored.NinjaFuturesContractMonth;

        // Live ConnectionState, mirrored into a property the XAML can read.
        _stateSub = _repository.ConnectionState.Subscribe(s => CurrentState = s);
    }

    public IReadOnlyList<string> AccountTypes { get; }
    public IReadOnlyList<MarketDataTypeOption> MarketDataTypes { get; }

    private BrokerConnectionMode ActiveMode =>
        _allModes.FirstOrDefault(m => m.Broker == SelectedBroker)
        ?? new BrokerConnectionMode(SelectedBroker, false, SelectedBroker.ToString(), string.Empty);

    public string ModeDisplayName => ActiveMode.DisplayName;
    public string ModeDescription => ActiveMode.Description;
    public bool IsDemoMode => !ActiveMode.IsLive;
    public bool IsLiveMode => ActiveMode.IsLive;

    public bool IsIbBrokerSelected => SelectedBroker == BrokerKind.InteractiveBrokers;
    public bool IsNinjaBrokerSelected => SelectedBroker == BrokerKind.NinjaTrader;

    public string SubtitleText => SelectedBroker switch
    {
        BrokerKind.InteractiveBrokers => "Sign in to your Interactive Brokers session",
        BrokerKind.NinjaTrader => "Connect to your NinjaTrader 8 session",
        _ => "Sign in",
    };

    [ObservableProperty]
    private BrokerKind _selectedBroker = BrokerKind.InteractiveBrokers;

    partial void OnSelectedBrokerChanged(BrokerKind value)
    {
        OnPropertyChanged(nameof(IsIbBrokerSelected));
        OnPropertyChanged(nameof(IsNinjaBrokerSelected));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(ModeDisplayName));
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(IsDemoMode));
        OnPropertyChanged(nameof(IsLiveMode));
    }

    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>
    /// Plain-text password. Set by <see cref="LoginWindow"/> code-behind from the
    /// <c>PasswordBox</c> (PasswordBox intentionally has no DependencyProperty for security).
    /// Stored locally only — TWS does its own authentication (including 2FA).
    /// </summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 7497;

    [ObservableProperty]
    private int _clientId = 1;

    [ObservableProperty]
    private string _accountType = "Paper";

    [ObservableProperty]
    private MarketDataTypeOption? _selectedMarketDataType;

    [ObservableProperty]
    private bool _rememberPassword;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    // ---- NinjaTrader-specific fields ----

    [ObservableProperty]
    private string _ninjaAccountName = "Sim101";

    [ObservableProperty]
    private string _ninjaDllPath = string.Empty;

    [ObservableProperty]
    private string _ninjaFuturesContractMonth = string.Empty;

    // ---- Connection state ----

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private ConnectionState _currentState = ConnectionState.Disconnected;

    /// <summary>Raised with <c>true</c> when a connection succeeded; <c>false</c> if the user cancelled.</summary>
    public event EventHandler<bool>? LoginCompleted;

    [RelayCommand]
    private void SelectIb() => SelectedBroker = BrokerKind.InteractiveBrokers;

    [RelayCommand]
    private void SelectNinja() => SelectedBroker = BrokerKind.NinjaTrader;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        StatusMessage = SelectedBroker == BrokerKind.InteractiveBrokers
            ? "Connecting to TWS..."
            : "Connecting to NinjaTrader...";
        IsConnecting = true;
        IsConnected = false;

        try
        {
            // Push the user-supplied connection settings into the live options instance for the
            // active broker. Then flip the selector so the connection manager picks the right client.
            if (SelectedBroker == BrokerKind.InteractiveBrokers)
            {
                _ibOptions.Host = Host;
                _ibOptions.Port = Port;
                _ibOptions.ClientId = ClientId;
                _ibOptions.AccountType = AccountType;
                _ibOptions.MarketDataType = SelectedMarketDataType?.Value ?? 1;
            }
            else
            {
                _ntOptions.AccountName = string.IsNullOrWhiteSpace(NinjaAccountName) ? "Sim101" : NinjaAccountName.Trim();
                _ntOptions.DllPath = NinjaDllPath?.Trim() ?? string.Empty;
                _ntOptions.DefaultFuturesContractMonth = NinjaFuturesContractMonth?.Trim() ?? string.Empty;
            }

            _brokerSelector.SetActive(SelectedBroker);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = _repository.ConnectionState.Subscribe(state =>
            {
                if (state == ConnectionState.Connected) tcs.TrySetResult(true);
                else if (state == ConnectionState.Failed) tcs.TrySetResult(false);
            });
            using var ctReg = cts.Token.Register(() => tcs.TrySetCanceled());

            await _repository.ConnectAsync(cts.Token);
            var ok = await tcs.Task.ConfigureAwait(true);

            if (!ok)
            {
                ErrorMessage = ResolveFailureMessage();
                StatusMessage = null;
                return;
            }

            PersistCredentials();
            var sessionAccount = SelectedBroker == BrokerKind.InteractiveBrokers ? AccountType : NinjaAccountName;
            _session.SetSignedIn(Username, sessionAccount);

            // Visibly show the "Connected" state for a moment so the user gets explicit feedback
            // before the window flips to the main shell.
            IsConnected = true;
            StatusMessage = "Connected · loading workspace...";
            await Task.Delay(700).ConfigureAwait(true);

            LoginCompleted?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = SelectedBroker == BrokerKind.InteractiveBrokers
                ? $"Connection timed out after 15s. Verify TWS / IB Gateway is running on {Host}:{Port} and that API access is enabled. " +
                  "If you have 2FA enabled, complete the 2FA prompt in TWS before signing in here."
                : "Connection timed out after 15s. Verify NinjaTrader 8 is running and the AT Interface is enabled in Tools → Options.";
            StatusMessage = null;
            _logger.LogWarning("Login connection timed out (broker={Broker})", SelectedBroker);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = null;
            _logger.LogError(ex, "Login connection failed");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => LoginCompleted?.Invoke(this, false);

    private string ResolveFailureMessage()
    {
        if (SelectedBroker == BrokerKind.InteractiveBrokers)
        {
            return ActiveMode.IsLive
                ? "TWS reported a connection failure. Common causes: API access not enabled in TWS Global Config, " +
                  "wrong port (TWS Paper=7497, TWS Live=7496, Gateway Paper=4002, Gateway Live=4001), " +
                  "or client id already in use by another connection."
                : "Connection failed in IB demo mode (this should be rare — check the log pane).";
        }
        return ActiveMode.IsLive
            ? "NinjaTrader reported a connection failure. Make sure NinjaTrader 8 is running, signed in, " +
              "and that Tools → Options → AT Interface → 'AT Interface enabled' is checked."
            : "Connection failed in NinjaTrader demo mode (this should be rare — check the log pane).";
    }

    private void PersistCredentials()
    {
        var stored = new StoredCredentials
        {
            SelectedBroker = SelectedBroker,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Host = Host,
            Port = Port,
            ClientId = ClientId,
            AccountType = AccountType,
            MarketDataType = SelectedMarketDataType?.Value ?? 1,
            RememberPassword = RememberPassword,
            NinjaAccountName = string.IsNullOrWhiteSpace(NinjaAccountName) ? "Sim101" : NinjaAccountName.Trim(),
            NinjaDllPath = NinjaDllPath?.Trim() ?? string.Empty,
            NinjaFuturesContractMonth = NinjaFuturesContractMonth?.Trim() ?? string.Empty,
        };
        if (RememberPassword && !string.IsNullOrEmpty(Password))
            stored.Password = Password;

        _credentialStore.Save(stored);
    }

    public void Dispose() => _stateSub.Dispose();
}

public sealed record MarketDataTypeOption(int Value, string DisplayName);
