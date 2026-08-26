using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to Kraken — the second of its two login rows.
///
/// <para>The secret is base64 as Kraken issues it — paste it unchanged.</para>
/// </summary>
public sealed class KeyedKrakenLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly KrakenOptions _options;

    public KeyedKrakenLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<KrakenOptions> options, ILogger<KeyedKrakenLoginFormViewModel> logger,
          IBrokerCredentialVerifier verifier)
        : base(selector, credentials, logger, verifier)
    {
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Kraken;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "Kraken";

    public override bool UsesPassphrase => false;

    public override bool UsesPrivateKeyPem => false;
}
