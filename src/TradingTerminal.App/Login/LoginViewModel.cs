using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

public sealed partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly InteractiveBrokersOptions _ibOptions;
    private readonly IMarketDataRepository _repository;
    private readonly CredentialStore _credentialStore;
    private readonly IbConnectionMode _connectionMode;
    private readonly SessionContext _session;
    private readonly ILogger<LoginViewModel> _logger;
    private readonly IDisposable _stateSub;

    public LoginViewModel(
        IOptions<InteractiveBrokersOptions> ibOptions,
        IMarketDataRepository repository,
        CredentialStore credentialStore,
        IbConnectionMode connectionMode,
        SessionContext session,
        ILogger<LoginViewModel> logger)
    {
        _ibOptions = ibOptions.Value;
        _repository = repository;
        _credentialStore = credentialStore;
        _connectionMode = connectionMode;
        _session = session;
        _logger = logger;

        AccountTypes = new[] { "Paper", "Live" };

        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        Host = stored.Host;
        Port = stored.Port;
        ClientId = stored.ClientId;
        AccountType = stored.AccountType;
        RememberPassword = stored.RememberPassword;
        Password = stored.Password ?? string.Empty;

        // Live ConnectionState, mirrored into a property the XAML can read.
        _stateSub = _repository.ConnectionState.Subscribe(s => CurrentState = s);
    }

    public IReadOnlyList<string> AccountTypes { get; }

    public string ModeDisplayName => _connectionMode.DisplayName;
    public string ModeDescription => _connectionMode.Description;
    public bool IsDemoMode => !_connectionMode.IsLive;
    public bool IsLiveMode => _connectionMode.IsLive;

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
    private bool _rememberPassword;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

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
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        StatusMessage = "Connecting to TWS...";
        IsConnecting = true;
        IsConnected = false;

        try
        {
            // Push the user-supplied connection settings into the live options instance so the
            // ConnectionManager picks them up on its next attempt.
            _ibOptions.Host = Host;
            _ibOptions.Port = Port;
            _ibOptions.ClientId = ClientId;
            _ibOptions.AccountType = AccountType;

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
            _session.SetSignedIn(Username, AccountType);

            // Visibly show the "Connected" state for a moment so the user gets explicit feedback
            // before the window flips to the main shell.
            IsConnected = true;
            StatusMessage = "Connected · loading workspace...";
            await Task.Delay(700).ConfigureAwait(true);

            LoginCompleted?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = $"Connection timed out after 15s. " +
                           $"Verify TWS / IB Gateway is running on {Host}:{Port} and that API access is enabled. " +
                           $"If you have 2FA enabled, complete the 2FA prompt in TWS before signing in here.";
            StatusMessage = null;
            _logger.LogWarning("Login connection timed out at {Host}:{Port}", Host, Port);
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
        return _connectionMode.IsLive
            ? "TWS reported a connection failure. Common causes: API access not enabled in TWS Global Config, " +
              "wrong port (TWS Paper=7497, TWS Live=7496, Gateway Paper=4002, Gateway Live=4001), " +
              "or client id already in use by another connection."
            : "Connection failed in demo mode (this should be rare — check the log pane).";
    }

    private void PersistCredentials()
    {
        var stored = new StoredCredentials
        {
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Host = Host,
            Port = Port,
            ClientId = ClientId,
            AccountType = AccountType,
            RememberPassword = RememberPassword,
        };
        if (RememberPassword && !string.IsNullOrEmpty(Password))
            stored.Password = Password;

        _credentialStore.Save(stored);
    }

    public void Dispose() => _stateSub.Dispose();
}
