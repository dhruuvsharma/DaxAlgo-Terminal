using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login.Forms;

public sealed class CTraderLoginFormViewModel : ViewModelBase, IBrokerLoginForm
{
    private readonly CTraderOptions _options;
    private readonly CredentialStore _credentialStore;

    public CTraderLoginFormViewModel(IOptions<CTraderOptions> options, CredentialStore credentialStore)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public BrokerKind Broker => BrokerKind.CTrader;
    public string DisplayName => "cTrader";

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _clientId = string.Empty;
    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }

    private string _clientSecret = string.Empty;
    public string ClientSecret { get => _clientSecret; set => SetProperty(ref _clientSecret, value); }

    private string _accessToken = string.Empty;
    public string AccessToken { get => _accessToken; set => SetProperty(ref _accessToken, value); }

    private long _accountId;
    public long AccountId { get => _accountId; set => SetProperty(ref _accountId, value); }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }

    public bool CanSubmit =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(AccessToken) &&
        AccountId != 0;

    public void ApplyToOptions()
    {
        _options.ClientId = ClientId?.Trim() ?? string.Empty;
        _options.ClientSecret = ClientSecret?.Trim() ?? string.Empty;
        _options.AccessToken = AccessToken?.Trim() ?? string.Empty;
        _options.CtidTraderAccountId = AccountId;
        _options.IsLive = IsLive;
        _options.Host = IsLive ? "live.ctraderapi.com" : "demo.ctraderapi.com";
        _options.Port = 5035;
    }

    public string GetSessionAccountLabel() => $"cTrader #{AccountId}";

    public string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s reaching {(IsLive ? "live" : "demo")}.ctraderapi.com. " +
        "Check your internet connection and that the OAuth credentials + ctidTraderAccountId are correct.";

    public string GetFailureMessage() =>
        "cTrader connection failed. Verify your OAuth client id, client secret, access token, and ctidTraderAccountId are correct, " +
        "and that the access token hasn't expired. Generate credentials at https://connect.spotware.com/apps.";

    public void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        ClientId = stored.CTraderClientId;
        ClientSecret = stored.CTraderClientSecret ?? string.Empty;
        AccessToken = stored.CTraderAccessToken ?? string.Empty;
        AccountId = stored.CTraderAccountId;
        IsLive = stored.CTraderIsLive;
    }

    public void Save()
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
