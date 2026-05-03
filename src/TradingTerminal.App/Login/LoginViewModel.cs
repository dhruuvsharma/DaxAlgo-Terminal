using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly InteractiveBrokersOptions _ibOptions;
    private readonly IMarketDataRepository _repository;
    private readonly CredentialStore _credentialStore;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(
        IOptions<InteractiveBrokersOptions> ibOptions,
        IMarketDataRepository repository,
        CredentialStore credentialStore,
        ILogger<LoginViewModel> logger)
    {
        _ibOptions = ibOptions.Value;
        _repository = repository;
        _credentialStore = credentialStore;
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
    }

    public IReadOnlyList<string> AccountTypes { get; }

    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>
    /// Plain-text password. Set by <see cref="LoginWindow"/> code-behind from the
    /// <c>PasswordBox</c> (PasswordBox intentionally has no DependencyProperty for security).
    /// Stored locally only — not transmitted to TWS, which authenticates separately.
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
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Raised with <c>true</c> when a connection succeeded; <c>false</c> if the user cancelled.</summary>
    public event EventHandler<bool>? LoginCompleted;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        StatusMessage = "Connecting to TWS...";
        IsConnecting = true;

        try
        {
            // Push the user-supplied connection settings into the live options instance so the
            // ConnectionManager picks them up on its next attempt.
            _ibOptions.Host = Host;
            _ibOptions.Port = Port;
            _ibOptions.ClientId = ClientId;
            _ibOptions.AccountType = AccountType;

            // Wait until connected (or fail with a timeout).
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
                ErrorMessage = "TWS reported a connection failure. Confirm TWS / IB Gateway is running and API access is enabled.";
                return;
            }

            PersistCredentials();
            StatusMessage = "Connected.";
            LoginCompleted?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Connection timed out. Is TWS running on " + Host + ":" + Port + "?";
            _logger.LogWarning("Login connection timed out at {Host}:{Port}", Host, Port);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Login connection failed");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => LoginCompleted?.Invoke(this, false);

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
}
