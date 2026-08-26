using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to Bybit — the second of its two login rows.
///
/// <para>Higher rate limits and the private channels order routing will need.</para>
/// </summary>
public sealed class KeyedBybitLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly BybitOptions _options;

    public KeyedBybitLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<BybitOptions> options, ILogger<KeyedBybitLoginFormViewModel> logger)
        : base(selector, credentials, logger)
    {
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Bybit;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "Bybit";

    public override bool UsesPassphrase => false;

    public override bool UsesPrivateKeyPem => false;
}
