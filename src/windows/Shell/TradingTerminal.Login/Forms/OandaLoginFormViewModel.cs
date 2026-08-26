using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// OANDA: a personal access token, an account id, and which environment they belong to.
///
/// <para>The account id is required rather than optional. Every v20 path is account-scoped — even
/// pricing — so a token alone reaches nothing, and making the field optional would produce a form that
/// submits happily and then fails on its first request with a message about a path.</para>
/// </summary>
public sealed class OandaLoginFormViewModel : BrokerLoginFormBase
{
    private readonly CredentialStore _credentials;
    private readonly OandaOptions _options;

    public OandaLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<OandaOptions> options, ILogger<OandaLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _credentials = credentials;
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Oanda;

    public override string DisplayName => "OANDA";

    private string _token = string.Empty;

    public string Token
    {
        get => _token;
        set { if (SetProperty(ref _token, value)) RaiseCanSubmit(); }
    }

    private string _accountId = string.Empty;

    /// <summary>The v20 account id, in the form <c>001-001-1234567-001</c>.</summary>
    public string AccountId
    {
        get => _accountId;
        set { if (SetProperty(ref _accountId, value)) RaiseCanSubmit(); }
    }

    private bool _practice = true;

    /// <summary>Practice by default. A first run should not point at live money.</summary>
    public bool Practice
    {
        get => _practice;
        set => SetProperty(ref _practice, value);
    }

    public override bool CanSubmit =>
        !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(AccountId);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void ApplyToOptions()
    {
        _options.AccountId = AccountId.Trim();
        _options.Practice = Practice;
    }

    public override string GetSessionAccountLabel() =>
        Practice ? $"OANDA · Practice · {AccountId}" : $"OANDA · Live · {AccountId}";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching OANDA. Check your internet connection.";

    public override string GetFailureMessage() =>
        "OANDA rejected the connection. A token issued for the other environment fails exactly like an "
        + "invalid one, so check whether this is a practice or a live token — and that the account id "
        + "belongs to the same environment.";

    public override void Load()
    {
        var record = _credentials.Load().KeysFor(Broker);
        AccountId = record.ApiKey;
        Token = record.ApiSecret ?? string.Empty;
        Practice = _options.Practice;
    }

    public override void Save()
    {
        var stored = _credentials.Load();
        stored.SelectedBroker = Broker;

        // The account id is an identifier, not a secret, so it is stored in the clear — which is what
        // lets the form show which account is configured without decrypting anything.
        stored.SetKeys(
            Broker,
            apiKey: AccountId.Trim(),
            secret: string.IsNullOrEmpty(Token) ? null : Token,
            passphrase: null);

        _credentials.Save(stored);
    }
}
