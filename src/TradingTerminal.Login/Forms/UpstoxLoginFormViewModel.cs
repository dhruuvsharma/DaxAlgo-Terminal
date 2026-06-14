using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.Upstox;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// Upstox login form: drives the OAuth2 authorization-code flow.
/// <list type="number">
/// <item><b>Authorize</b> opens the Upstox sign-in dialog in the browser (built from the API key +
/// redirect URI).</item>
/// <item>After approving, the user copies the one-time <c>code</c> from the redirected URL into the
/// <see cref="AuthCode"/> box.</item>
/// <item><b>Get token</b> exchanges that code for an <see cref="AccessToken"/> via
/// <see cref="IUpstoxAuthService"/>.</item>
/// <item><b>Connect</b> then uses the token. Tokens expire daily (~03:30 IST), so this is repeated
/// each trading day.</item>
/// </list>
/// </summary>
public sealed class UpstoxLoginFormViewModel : BrokerLoginFormBase
{
    private readonly UpstoxOptions _options;
    private readonly CredentialStore _credentialStore;
    private readonly IUpstoxAuthService _auth;

    public UpstoxLoginFormViewModel(
        IOptions<UpstoxOptions> options,
        CredentialStore credentialStore,
        IUpstoxAuthService auth,
        IBrokerSelector selector,
        ILogger<UpstoxLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
        _auth = auth;

        AuthorizeCommand = new RelayCommand(OpenAuthorizeUrl, CanAuthorize);
        ExchangeCodeCommand = new AsyncRelayCommand(ExchangeCodeAsync, CanExchange);
    }

    public override BrokerKind Broker => BrokerKind.Upstox;
    public override string DisplayName => "Upstox";

    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) RaiseGateChanged(); }
    }

    private string _apiSecret = string.Empty;
    public string ApiSecret
    {
        get => _apiSecret;
        set { if (SetProperty(ref _apiSecret, value)) RaiseGateChanged(); }
    }

    private string _redirectUri = string.Empty;
    public string RedirectUri
    {
        get => _redirectUri;
        set { if (SetProperty(ref _redirectUri, value)) RaiseGateChanged(); }
    }

    private string _authCode = string.Empty;
    public string AuthCode
    {
        get => _authCode;
        set { if (SetProperty(ref _authCode, value)) ExchangeCodeCommand.NotifyCanExecuteChanged(); }
    }

    private string _accessToken = string.Empty;
    public string AccessToken
    {
        get => _accessToken;
        set { if (SetProperty(ref _accessToken, value)) RaiseCanSubmit(); }
    }

    /// <summary>Status text shown beneath the auth buttons (success or a user-facing error).</summary>
    private string? _authMessage;
    public string? AuthMessage
    {
        get => _authMessage;
        private set => SetProperty(ref _authMessage, value);
    }

    private bool _isExchanging;
    public bool IsExchanging
    {
        get => _isExchanging;
        private set { if (SetProperty(ref _isExchanging, value)) ExchangeCodeCommand.NotifyCanExecuteChanged(); }
    }

    public IRelayCommand AuthorizeCommand { get; }
    public IAsyncRelayCommand ExchangeCodeCommand { get; }

    public override bool CanSubmit => !string.IsNullOrWhiteSpace(AccessToken);

    private bool CanAuthorize() =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(RedirectUri);

    private bool CanExchange() =>
        !IsExchanging &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ApiSecret) &&
        !string.IsNullOrWhiteSpace(RedirectUri) &&
        !string.IsNullOrWhiteSpace(AuthCode);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void RaiseGateChanged()
    {
        AuthorizeCommand.NotifyCanExecuteChanged();
        ExchangeCodeCommand.NotifyCanExecuteChanged();
    }

    private void OpenAuthorizeUrl()
    {
        try
        {
            var url = _auth.BuildAuthorizationUrl(_options.BaseUrl, ApiKey.Trim(), RedirectUri.Trim());
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AuthMessage = "Browser opened — sign in, then paste the 'code' query parameter from the redirected URL below.";
        }
        catch (Exception ex)
        {
            AuthMessage = $"Couldn't open the browser: {ex.Message}. Copy the URL manually from the logs.";
            Logger.LogWarning(ex, "Upstox: failed to launch authorization URL");
        }
    }

    private async Task ExchangeCodeAsync()
    {
        AuthMessage = null;
        IsExchanging = true;
        try
        {
            var token = await _auth.ExchangeCodeForTokenAsync(
                _options.BaseUrl, ApiKey.Trim(), ApiSecret.Trim(), RedirectUri.Trim(), AuthCode.Trim())
                .ConfigureAwait(true);
            AccessToken = token;
            AuthMessage = "Access token obtained — you can Connect now. (Tokens expire daily ~03:30 IST.)";
        }
        catch (Exception ex)
        {
            AuthMessage = ex.Message;
            Logger.LogWarning(ex, "Upstox token exchange failed");
        }
        finally
        {
            IsExchanging = false;
        }
    }

    public override void ApplyToOptions()
    {
        _options.ApiKey = ApiKey?.Trim() ?? string.Empty;
        _options.ApiSecret = ApiSecret?.Trim() ?? string.Empty;
        _options.RedirectUri = RedirectUri?.Trim() ?? string.Empty;
        _options.AccessToken = AccessToken?.Trim() ?? string.Empty;
    }

    public override string GetSessionAccountLabel() => "Upstox";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching api.upstox.com. Check your internet connection and that the access token is still valid (they expire daily ~03:30 IST).";

    public override string GetFailureMessage() =>
        "Upstox connection failed. The access token may have expired (they reset daily ~03:30 IST) — re-run Authorize → Get token, then Connect again.";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        ApiKey = stored.UpstoxApiKey;
        RedirectUri = stored.UpstoxRedirectUri;
        ApiSecret = stored.UpstoxApiSecret ?? string.Empty;
        AccessToken = stored.UpstoxAccessToken ?? string.Empty;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.Upstox;
        stored.UpstoxApiKey = ApiKey?.Trim() ?? string.Empty;
        stored.UpstoxRedirectUri = RedirectUri?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(ApiSecret)) stored.UpstoxApiSecret = ApiSecret.Trim();
        if (!string.IsNullOrEmpty(AccessToken)) stored.UpstoxAccessToken = AccessToken.Trim();
        _credentialStore.Save(stored);
    }
}
