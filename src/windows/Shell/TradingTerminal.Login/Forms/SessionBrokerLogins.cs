using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// One sign-in broker's login row, described rather than hand-written: which fields it asks for, what the
/// broker calls them, and how its sign-in completes.
///
/// <para>A null label hides the field. Every field maps to one slot of the stored record — key (clear),
/// secret and passphrase (encrypted), account and extra (clear) — and the session a sign-in issues is
/// stored beside them.</para>
/// </summary>
public sealed record SessionBrokerLogin(BrokerKind Broker, string Name, SignInStyle Style, string Help)
{
    public string? KeyLabel { get; init; }

    public string? SecretLabel { get; init; }

    public string? PassphraseLabel { get; init; }

    public string? AccountLabel { get; init; }

    public string? ExtraLabel { get; init; }

    /// <summary>What the user brings back: a redirected address, an authenticator code, a token.
    /// Null for a <see cref="SignInStyle.Credentials"/> sign-in, which needs nothing.</summary>
    public string? ProofLabel { get; init; }

    /// <summary>True when the sign-in URL carries the redirect, so the form must ask for it.</summary>
    public bool AsksRedirect { get; init; }

    /// <summary>True when the passphrase slot holds an authenticator setup key, from which the code can be
    /// computed instead of typed.</summary>
    public bool PassphraseIsTotpKey { get; init; }

    /// <summary>How long a session lasts, for the "signed in … ago" note.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(18);
}

/// <summary>The sign-in brokers whose rows are described here, and their registration.</summary>
public static class SessionBrokerLogins
{
    public static IReadOnlyList<SessionBrokerLogin> All { get; } =
    [
        // ── India ──
        new(BrokerKind.Zerodha, "Zerodha", SignInStyle.Browser,
            "Create a Kite Connect app at developers.kite.trade (a paid subscription) and set its redirect URL. "
            + "Sign in each morning: Kite sessions end at 6 AM.")
        {
            KeyLabel = "API key", SecretLabel = "API secret",
            ProofLabel = "Redirected address (or its request_token)",
        },

        new(BrokerKind.AngelOne, "Angel One", SignInStyle.Code,
            "Create a SmartAPI app at smartapi.angelbroking.com and turn on TOTP for your account. Type the "
            + "authenticator's six digits below — or store its setup key and the terminal computes them; that key "
            + "is as sensitive as the phone app holding it.")
        {
            KeyLabel = "SmartAPI key", AccountLabel = "Client code", SecretLabel = "PIN",
            PassphraseLabel = "Authenticator setup key (optional)", PassphraseIsTotpKey = true,
            ProofLabel = "Authenticator code",
        },

        new(BrokerKind.Dhan, "Dhan", SignInStyle.Token,
            "Generate an access token on web.dhan.co under DhanHQ Trading APIs and paste it with your client id. "
            + "Market data may need Dhan's data plan.")
        {
            AccountLabel = "Client id", ProofLabel = "Access token",
            SessionLifetime = TimeSpan.FromDays(30),
        },

        new(BrokerKind.Fyers, "Fyers", SignInStyle.Browser,
            "Create an app at myapi.fyers.in with the redirect URL below, then sign in each day.")
        {
            KeyLabel = "App id (XXXX-100)", SecretLabel = "Secret id",
            ProofLabel = "Redirected address (or its auth_code)", AsksRedirect = true,
        },

        new(BrokerKind.FivePaisa, "5paisa", SignInStyle.Code,
            "Copy the user key, encryption key and user id from 5paisa's API page (xstream.5paisa.com), and "
            + "turn on TOTP for your account.")
        {
            KeyLabel = "User key", SecretLabel = "Encryption key", ExtraLabel = "App user id",
            AccountLabel = "Client code", PassphraseLabel = "PIN", ProofLabel = "Authenticator code",
        },

        new(BrokerKind.AliceBlue, "Alice Blue", SignInStyle.Credentials,
            "Generate an API key in ANT Web under Apps → API key. Signing in needs nothing more.")
        {
            AccountLabel = "User id", SecretLabel = "API key",
        },

        new(BrokerKind.IciciBreeze, "ICICI Breeze", SignInStyle.Browser,
            "Register an app at api.icicidirect.com. Sign in each day.")
        {
            KeyLabel = "App key", SecretLabel = "Secret key",
            ProofLabel = "Redirected address (or its apisession)",
        },

        // ── US and global ──
        new(BrokerKind.CharlesSchwab, "Charles Schwab", SignInStyle.Browser,
            "Create an app at developer.schwab.com with Market Data and Accounts access, and give it the callback "
            + "below exactly. A sign-in lasts seven days; the terminal renews the half-hour tokens itself.")
        {
            KeyLabel = "App key", SecretLabel = "Secret",
            ProofLabel = "Redirected address (or its code)", AsksRedirect = true,
            SessionLifetime = TimeSpan.FromDays(7),
        },

        new(BrokerKind.TradeStation, "TradeStation", SignInStyle.Browser,
            "Request an API key from TradeStation (Client Center → API) with the callback below. Only market-data "
            + "and read-account scopes are asked for.")
        {
            KeyLabel = "API key", SecretLabel = "API secret",
            ProofLabel = "Redirected address (or its code)", AsksRedirect = true,
            SessionLifetime = TimeSpan.FromDays(30),
        },

        new(BrokerKind.Tastytrade, "tastytrade", SignInStyle.Token,
            "On my.tastytrade.com open Manage → My Profile → API → OAuth Applications, create an app, then Create "
            + "Grant and paste its refresh token below with the app's client secret. The grant does not expire.")
        {
            KeyLabel = "Client id (optional)", SecretLabel = "Client secret", ProofLabel = "Refresh token (from Create Grant)",
            SessionLifetime = TimeSpan.FromDays(3650),
        },

        new(BrokerKind.ETrade, "E*TRADE", SignInStyle.Browser,
            "Request a consumer key at developer.etrade.com. Open the sign-in page, approve, and paste the short "
            + "code E*TRADE shows. Sessions end at midnight US Eastern.")
        {
            KeyLabel = "Consumer key", SecretLabel = "Consumer secret", ProofLabel = "Verification code",
            SessionLifetime = TimeSpan.FromHours(12),
        },

        new(BrokerKind.Tradovate, "Tradovate", SignInStyle.Credentials,
            "Buy the API add-on in Tradovate and generate an API key (cid and secret). Market data also needs a CME "
            + "data subscription. Type demo in Environment for a demo account.")
        {
            AccountLabel = "User name", PassphraseLabel = "Password", KeyLabel = "API cid", SecretLabel = "API secret",
            ExtraLabel = "Environment (live or demo, optional)",
            SessionLifetime = TimeSpan.FromHours(1),
        },

        new(BrokerKind.SaxoBank, "Saxo Bank", SignInStyle.Browser,
            "Register an app at developer.saxo with the redirect below. Saxo's refresh tokens last an hour, so a "
            + "terminal closed longer than that needs a new sign-in.")
        {
            KeyLabel = "App key", SecretLabel = "App secret",
            ProofLabel = "Redirected address (or its code)", AsksRedirect = true,
            SessionLifetime = TimeSpan.FromHours(1),
        },

        new(BrokerKind.IgGroup, "IG", SignInStyle.Credentials,
            "Create an API key under My IG → Settings → API keys. Type demo in Environment for a demo account. "
            + "History is metered at 10,000 points a week, so charts load a limited past.")
        {
            KeyLabel = "API key", AccountLabel = "User name", SecretLabel = "Password",
            ExtraLabel = "Environment (live or demo, optional)",
            SessionLifetime = TimeSpan.FromHours(6),
        },

        new(BrokerKind.Questrade, "Questrade", SignInStyle.Token,
            "In Questrade's App Hub register a personal app and generate a manual refresh token, then paste it. Each "
            + "token works once — the terminal stores the new one Questrade returns.")
        {
            ProofLabel = "Refresh token",
            SessionLifetime = TimeSpan.FromDays(7),
        },

        new(BrokerKind.RobinhoodCrypto, "Robinhood (crypto)", SignInStyle.Credentials,
            "Create an API key in Robinhood's crypto account settings, registering the public half of an Ed25519 key "
            + "pair. Paste the API key and the base64 private key; the private key never leaves this machine.")
        {
            KeyLabel = "API key", SecretLabel = "Private key (base64)",
            SessionLifetime = TimeSpan.FromDays(3650),
        },
    ];

    /// <summary>Registers every described row.</summary>
    public static IServiceCollection AddSessionBrokerLogins(this IServiceCollection services)
    {
        foreach (var broker in All)
            services.AddSingleton<IBrokerLoginForm>(sp => new SessionBrokerLoginFormViewModel(
                broker, sp.GetRequiredService<IBrokerSelector>(), sp.GetRequiredService<CredentialStore>(),
                sp.GetService<IBrokerSessionIssuer>() ?? IBrokerSessionIssuer.None,
                sp.GetRequiredService<ILogger<SessionBrokerLoginFormViewModel>>()));
        return services;
    }
}

/// <summary>
/// The login row for a broker with a sign-in step.
///
/// <para>Connect is available once a session exists; <b>Sign in</b> is what makes one. For a browser sign-in
/// the row opens the broker's page and takes back whatever the user pastes — the whole redirected address
/// is fine. The session is stored before connecting, because the client reads it from the store and
/// nowhere else (the trap Tradier and OANDA fell into).</para>
/// </summary>
public sealed class SessionBrokerLoginFormViewModel : BrokerLoginFormBase
{
    private readonly SessionBrokerLogin _broker;
    private readonly CredentialStore _store;
    private readonly IBrokerSessionIssuer _issuer;
    private string _session = string.Empty;
    private DateTimeOffset? _issuedUtc;

    /// <summary>True once this row has signed in since it loaded — only then is its session the one to store.</summary>
    private bool _signedInHere;

    public SessionBrokerLoginFormViewModel(
        SessionBrokerLogin broker, IBrokerSelector selector, CredentialStore store, IBrokerSessionIssuer issuer,
        ILogger<SessionBrokerLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _broker = broker;
        _store = store;
        _issuer = issuer;
        OpenSignInPageCommand = new AsyncRelayCommand(OpenSignInPageAsync, () => IsBrowser && !IsSigningIn && !string.IsNullOrWhiteSpace(ApiKey));
        SignInCommand = new AsyncRelayCommand(SignInAsync, CanSignIn);
    }

    public SessionBrokerLogin Description => _broker;

    public override BrokerKind Broker => _broker.Broker;

    public override string DisplayName => _broker.Name;

    public string Help => _broker.Help;

    public bool IsBrowser => _broker.Style == SignInStyle.Browser;

    public bool AsksProof => _broker.ProofLabel is not null;

    public bool AsksRedirect => _broker.AsksRedirect;

    public string? KeyLabel => _broker.KeyLabel;
    public string? SecretLabel => _broker.SecretLabel;
    public string? PassphraseLabel => _broker.PassphraseLabel;
    public string? AccountLabel => _broker.AccountLabel;
    public string? ExtraLabel => _broker.ExtraLabel;
    public string? ProofLabel => _broker.ProofLabel;

    public bool AsksKey => KeyLabel is not null;
    public bool AsksSecret => SecretLabel is not null;
    public bool AsksPassphrase => PassphraseLabel is not null;
    public bool AsksAccount => AccountLabel is not null;
    public bool AsksExtra => ExtraLabel is not null;

    private string _apiKey = string.Empty;
    public string ApiKey { get => _apiKey; set { if (SetProperty(ref _apiKey, value)) RaiseCommands(); } }

    private string _apiSecret = string.Empty;
    public string ApiSecret { get => _apiSecret; set { if (SetProperty(ref _apiSecret, value)) RaiseCommands(); } }

    private string _passphrase = string.Empty;
    public string Passphrase { get => _passphrase; set { if (SetProperty(ref _passphrase, value)) RaiseCommands(); } }

    private string _account = string.Empty;
    public string Account { get => _account; set { if (SetProperty(ref _account, value)) RaiseCommands(); } }

    private string _extra = string.Empty;
    public string Extra { get => _extra; set { if (SetProperty(ref _extra, value)) RaiseCommands(); } }

    private string _proof = string.Empty;
    /// <summary>What the user brought back from the sign-in.</summary>
    public string Proof { get => _proof; set { if (SetProperty(ref _proof, value)) RaiseCommands(); } }

    private string _redirectUri = "https://127.0.0.1/";
    public string RedirectUri { get => _redirectUri; set { if (SetProperty(ref _redirectUri, value)) RaiseCommands(); } }

    private string? _signInMessage;
    public string? SignInMessage { get => _signInMessage; private set => SetProperty(ref _signInMessage, value); }

    private bool _isSigningIn;
    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set { if (SetProperty(ref _isSigningIn, value)) RaiseCommands(); }
    }

    public bool HasSession => !string.IsNullOrWhiteSpace(_session);

    public IAsyncRelayCommand OpenSignInPageCommand { get; }

    public IAsyncRelayCommand SignInCommand { get; }

    public override bool CanSubmit => HasSession;

    /// <summary>The credential the issuer is handed: every field as typed.</summary>
    private BrokerCredential App => new(ApiKey.Trim(), ApiSecret.Trim(), Passphrase.Trim())
    {
        Account = Account.Trim(),
        Extra = Extra.Trim(),
    };

    /// <summary>A shown field must be filled unless its label says it is optional.</summary>
    private static bool Filled(string? label, string value) =>
        label is null || label.Contains("optional", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(value);

    private bool CanSignIn() =>
        !IsSigningIn
        && Filled(KeyLabel, ApiKey) && Filled(SecretLabel, ApiSecret) && Filled(PassphraseLabel, Passphrase)
        && Filled(AccountLabel, Account) && Filled(ExtraLabel, Extra)
        && (_broker.Style == SignInStyle.Credentials
            || !string.IsNullOrWhiteSpace(Proof)
            // A code sign-in can compute the code from a stored setup key instead of a typed one.
            || (_broker.PassphraseIsTotpKey && !string.IsNullOrWhiteSpace(Passphrase)));

    private void RaiseCommands()
    {
        OpenSignInPageCommand.NotifyCanExecuteChanged();
        SignInCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The sign-in page, or null when this broker has none or no key is entered yet. Building it can
    /// take a call — E*TRADE issues a request token before there is a page to open.</summary>
    public Task<string?> SignInUrlAsync() =>
        IsBrowser && !string.IsNullOrWhiteSpace(ApiKey)
            ? _issuer.SignInUrlAsync(Broker, App, RedirectUri.Trim())
            : Task.FromResult<string?>(null);

    private async Task OpenSignInPageAsync()
    {
        if (await SignInUrlAsync().ConfigureAwait(true) is not { } url)
        {
            SignInMessage = _issuer.Issues(Broker)
                ? $"{_broker.Name} did not give a sign-in page — check the key above, then try again."
                : "This build has no sign-in wired for " + _broker.Name + ".";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SignInMessage = "Browser opened. Sign in there, then paste the address it lands on below and press Sign in.";
        }
        catch (Exception ex)
        {
            SignInMessage = $"Could not open a browser ({ex.Message}). Open this address yourself: {url}";
        }
    }

    private async Task SignInAsync()
    {
        IsSigningIn = true;
        SignInMessage = null;
        try
        {
            var issue = await _issuer.SignInAsync(Broker, App, Proof, RedirectUri.Trim()).ConfigureAwait(true);
            if (!issue.Ok)
            {
                SignInMessage = $"{_broker.Name} refused the sign-in: {issue.Detail}";
                return;
            }

            _session = issue.Session;
            _issuedUtc = DateTimeOffset.UtcNow;
            _signedInHere = true;
            if (!string.IsNullOrWhiteSpace(issue.Account) && string.IsNullOrWhiteSpace(Account)) Account = issue.Account;
            Proof = string.Empty;
            Save();
            SignInMessage = $"Signed in to {_broker.Name}. Connect when ready.";
            OnPropertyChanged(nameof(HasSession));
            OnPropertyChanged(nameof(CanSubmit));
            ConnectCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>Stores everything before connecting — the client reads the session from the store.</summary>
    public override void ApplyToOptions() => Save();

    public override string GetSessionAccountLabel() =>
        string.IsNullOrWhiteSpace(Account) ? _broker.Name : $"{_broker.Name} · {Account.Trim()}";

    public override string GetTimeoutErrorMessage() => $"Connection timed out reaching {_broker.Name}. Check your internet connection.";

    public override string GetFailureMessage() =>
        $"{_broker.Name} refused the session. Sessions expire (usually overnight) — press Sign in again.";

    public override void Load()
    {
        var record = _store.Load().KeysFor(Broker);
        ApiKey = record.ApiKey ?? string.Empty;
        ApiSecret = record.ApiSecret ?? string.Empty;
        Passphrase = record.Passphrase ?? string.Empty;
        Account = record.Account ?? string.Empty;
        Extra = record.Extra ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(record.RedirectUri)) RedirectUri = record.RedirectUri;
        _session = record.Session ?? string.Empty;
        _issuedUtc = record.SessionIssuedUtc;
        _signedInHere = false;

        if (HasSession && _issuedUtc is { } issued)
            SignInMessage = DateTimeOffset.UtcNow - issued > _broker.SessionLifetime
                ? $"The stored session is from {issued.ToLocalTime():g} and has probably expired — sign in again."
                : $"Signed in at {issued.ToLocalTime():g}.";
    }

    /// <summary>
    /// Stores the row. The session written is the one this row signed in for — or, when it has not signed in
    /// since it loaded and the key is unchanged, whatever is stored <i>now</i>: a running client may have
    /// renewed it meanwhile, and for a broker that rotates its refresh token (Questrade, Saxo) writing back
    /// the copy loaded when the window opened would store a dead one.
    /// </summary>
    public override void Save()
    {
        var stored = _store.Load();
        var previous = stored.KeysFor(Broker);
        if (!_signedInHere && string.Equals(previous.ApiKey ?? string.Empty, ApiKey.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(previous.Session))
        {
            _session = previous.Session!;
            _issuedUtc = previous.SessionIssuedUtc ?? _issuedUtc;
        }

        stored.SelectedBroker = Broker;
        stored.SetKeys(Broker, ApiKey.Trim(),
            string.IsNullOrWhiteSpace(ApiSecret) ? null : ApiSecret.Trim(),
            string.IsNullOrWhiteSpace(Passphrase) ? null : Passphrase.Trim());
        stored.SetSession(Broker, Account.Trim(), Extra.Trim(), HasSession ? _session : null, _issuedUtc ?? DateTimeOffset.UtcNow);
        stored.KeysFor(Broker).RedirectUri = RedirectUri.Trim();
        _store.Save(stored);
    }
}
