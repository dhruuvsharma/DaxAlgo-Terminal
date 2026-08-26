using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// Tradier: an access token and which environment it belongs to.
///
/// <para>Two fields, because that is all the API needs. The environment toggle is not a convenience —
/// sandbox and production are different hosts issuing different tokens, and the wrong pairing is
/// refused exactly like an invalid token, so a user who cannot say which they hold is stuck with an
/// error that names nothing.</para>
/// </summary>
public sealed class TradierLoginFormViewModel : BrokerLoginFormBase
{
    private readonly CredentialStore _credentials;
    private readonly TradierOptions _options;

    public TradierLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<TradierOptions> options, ILogger<TradierLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _credentials = credentials;
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Tradier;

    public override string DisplayName => "Tradier";

    private string _token = string.Empty;

    /// <summary>The access token.</summary>
    public string Token
    {
        get => _token;
        set { if (SetProperty(ref _token, value)) RaiseCanSubmit(); }
    }

    private bool _sandbox = true;

    /// <summary>Sandbox by default: free, immediate, and needs no funded account.</summary>
    public bool Sandbox
    {
        get => _sandbox;
        set => SetProperty(ref _sandbox, value);
    }

    public override bool CanSubmit => !string.IsNullOrWhiteSpace(Token);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void ApplyToOptions() => _options.Sandbox = Sandbox;

    public override string GetSessionAccountLabel() =>
        Sandbox ? "Tradier · Sandbox" : "Tradier · Production";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Tradier. Check your internet connection.";

    public override string GetFailureMessage() =>
        "Tradier rejected the connection. Sandbox and production use separate tokens against separate "
        + "hosts, and the wrong pairing fails exactly like an invalid token — check which one this is. "
        + "A free sandbox token is issued immediately at developer.tradier.com.";

    public override void Load()
    {
        var record = _credentials.Load().KeysFor(Broker);
        Token = record.ApiSecret ?? string.Empty;
        Sandbox = !string.Equals(record.ApiKey, "production", StringComparison.OrdinalIgnoreCase);
    }

    public override void Save()
    {
        var stored = _credentials.Load();
        stored.SelectedBroker = Broker;

        // The token is the secret, so it goes in the encrypted slot. There is no separate key here —
        // Tradier authenticates with a bearer token alone, and the environment rides in the clear
        // because it is a choice rather than a credential.
        stored.SetKeys(
            Broker,
            apiKey: Sandbox ? "sandbox" : "production",
            secret: string.IsNullOrEmpty(Token) ? null : Token,
            passphrase: null);

        _credentials.Save(stored);
    }
}
