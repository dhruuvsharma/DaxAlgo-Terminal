using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.CTrader;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

public sealed class CTraderLoginFormViewModel : BrokerLoginFormBase
{
    private readonly CTraderOptions _options;
    private readonly CredentialStore _credentialStore;
    private readonly ICTraderAccountDiscovery _discovery;

    public CTraderLoginFormViewModel(
        IOptions<CTraderOptions> options,
        CredentialStore credentialStore,
        ICTraderAccountDiscovery discovery,
        IBrokerSelector selector,
        ILogger<CTraderLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
        _discovery = discovery;

        DiscoverAccountsCommand = new AsyncRelayCommand(DiscoverAccountsAsync, CanDiscover);
    }

    public override BrokerKind Broker => BrokerKind.CTrader;
    public override string DisplayName => "cTrader";

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _clientId = string.Empty;
    public string ClientId
    {
        get => _clientId;
        set { if (SetProperty(ref _clientId, value)) RaiseCanSubmitAndDiscover(); }
    }

    private string _clientSecret = string.Empty;
    public string ClientSecret
    {
        get => _clientSecret;
        set { if (SetProperty(ref _clientSecret, value)) RaiseCanSubmitAndDiscover(); }
    }

    private string _accessToken = string.Empty;
    public string AccessToken
    {
        get => _accessToken;
        set { if (SetProperty(ref _accessToken, value)) RaiseCanSubmitAndDiscover(); }
    }

    private long _accountId;
    public long AccountId
    {
        get => _accountId;
        set { if (SetProperty(ref _accountId, value)) RaiseCanSubmit(); }
    }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }

    // ---- Discover-accounts state ---------------------------------------------------------------

    /// <summary>True while <see cref="DiscoverAccountsCommand"/> is in flight. Drives the spinner
    /// next to the Discover button and disables the button itself.</summary>
    private bool _isDiscovering;
    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set
        {
            if (SetProperty(ref _isDiscovering, value))
                DiscoverAccountsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Status text shown beneath the AccountId field after a discovery attempt.
    /// Either success ("Discovered N account(s)…") or a user-facing error.</summary>
    private string? _discoveryMessage;
    public string? DiscoveryMessage
    {
        get => _discoveryMessage;
        private set => SetProperty(ref _discoveryMessage, value);
    }

    /// <summary>Discovered accounts. Populated when more than one account matches the access token,
    /// so the XAML can show a picker. Empty when the discovery returned a single account
    /// (which is auto-applied to <see cref="AccountId"/> immediately).</summary>
    public ObservableCollection<CTraderDiscoveredAccount> DiscoveredAccounts { get; } = new();

    /// <summary>Drives the visibility of the discovered-accounts picker in XAML — bound through
    /// <c>BooleanToVisibilityConverter</c>. True only when there are multiple candidates to pick
    /// from (the single-account case auto-applies and never populates the collection).</summary>
    public bool HasDiscoveredAccounts => DiscoveredAccounts.Count > 0;

    private CTraderDiscoveredAccount? _selectedDiscoveredAccount;
    public CTraderDiscoveredAccount? SelectedDiscoveredAccount
    {
        get => _selectedDiscoveredAccount;
        set
        {
            if (SetProperty(ref _selectedDiscoveredAccount, value) && value is not null)
            {
                AccountId = value.AccountId;
                IsLive = value.IsLive;
            }
        }
    }

    public IAsyncRelayCommand DiscoverAccountsCommand { get; }

    public override bool CanSubmit =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(AccessToken) &&
        AccountId != 0;

    private bool CanDiscover() =>
        !IsDiscovering &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(AccessToken);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCanSubmitAndDiscover()
    {
        RaiseCanSubmit();
        DiscoverAccountsCommand.NotifyCanExecuteChanged();
    }

    private async Task DiscoverAccountsAsync()
    {
        DiscoveryMessage = null;
        DiscoveredAccounts.Clear();
        OnPropertyChanged(nameof(HasDiscoveredAccounts));
        SelectedDiscoveredAccount = null;
        IsDiscovering = true;
        try
        {
            // The access token is bound to one environment — pick the endpoint that matches the
            // user's current Live/Demo selection. If they get this wrong Spotware returns 0 accounts
            // and the error message below points them at the checkbox.
            var host = IsLive ? "live.ctraderapi.com" : "demo.ctraderapi.com";
            var accounts = await _discovery.DiscoverAsync(
                host,
                port: 5035,
                clientId: ClientId.Trim(),
                clientSecret: ClientSecret.Trim(),
                accessToken: AccessToken.Trim()).ConfigureAwait(true);

            if (accounts.Count == 0)
            {
                DiscoveryMessage =
                    $"No cTrader accounts attached to this access token on the {(IsLive ? "live" : "demo")} endpoint. " +
                    "Toggle Live/Demo if you generated the token in the other environment, or regenerate it at connect.spotware.com/apps.";
                return;
            }

            if (accounts.Count == 1)
            {
                var only = accounts[0];
                AccountId = only.AccountId;
                IsLive = only.IsLive;
                DiscoveryMessage = $"Found 1 account: #{only.AccountId} ({(only.IsLive ? "Live" : "Demo")}).";
                return;
            }

            foreach (var a in accounts)
                DiscoveredAccounts.Add(a);
            OnPropertyChanged(nameof(HasDiscoveredAccounts));
            DiscoveryMessage = $"Found {accounts.Count} accounts — pick one below to populate the AccountId field.";
        }
        catch (OperationCanceledException)
        {
            DiscoveryMessage = "Discovery timed out reaching Spotware. Check your internet connection and Live/Demo selection.";
            Logger.LogWarning("cTrader account discovery timed out");
        }
        catch (Exception ex)
        {
            DiscoveryMessage = $"Discovery failed: {ex.Message}";
            Logger.LogWarning(ex, "cTrader account discovery failed");
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    public override void ApplyToOptions()
    {
        _options.ClientId = ClientId?.Trim() ?? string.Empty;
        _options.ClientSecret = ClientSecret?.Trim() ?? string.Empty;
        _options.AccessToken = AccessToken?.Trim() ?? string.Empty;
        _options.CtidTraderAccountId = AccountId;
        _options.IsLive = IsLive;
        _options.Host = IsLive ? "live.ctraderapi.com" : "demo.ctraderapi.com";
        _options.Port = 5035;
    }

    public override string GetSessionAccountLabel() => $"cTrader #{AccountId}";

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s reaching {(IsLive ? "live" : "demo")}.ctraderapi.com. " +
        "Check your internet connection and that the OAuth credentials + ctidTraderAccountId are correct.";

    public override string GetFailureMessage() =>
        "cTrader connection failed. Verify your OAuth client id, client secret, access token, and ctidTraderAccountId are correct, " +
        "and that the access token hasn't expired. Generate credentials at https://connect.spotware.com/apps.";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        ClientId = stored.CTraderClientId;
        ClientSecret = stored.CTraderClientSecret ?? string.Empty;
        AccessToken = stored.CTraderAccessToken ?? string.Empty;
        AccountId = stored.CTraderAccountId;
        IsLive = stored.CTraderIsLive;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.CTrader;
        stored.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        stored.CTraderClientId = ClientId?.Trim() ?? string.Empty;
        stored.CTraderAccountId = AccountId;
        stored.CTraderIsLive = IsLive;
        if (!string.IsNullOrEmpty(ClientSecret)) stored.CTraderClientSecret = ClientSecret;
        if (!string.IsNullOrEmpty(AccessToken)) stored.CTraderAccessToken = AccessToken;
        _credentialStore.Save(stored);
    }
}
