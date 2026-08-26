using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to OKX — the second of its two login rows.
///
/// <para>OKX keys carry a passphrase you chose when creating the key. It is not the account password.</para>
/// </summary>
public sealed class KeyedOkxLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly OkxOptions _options;

    public KeyedOkxLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<OkxOptions> options, ILogger<KeyedOkxLoginFormViewModel> logger)
        : base(selector, credentials, logger)
    {
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Okx;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "OKX";

    public override bool UsesPassphrase => true;

    public override bool UsesPrivateKeyPem => false;
}
