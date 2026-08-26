using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to Binance — the second of its two login rows.
///
/// <para>Higher REST weight limits. Market data itself stays public.</para>
/// </summary>
public sealed class KeyedBinanceLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly BinanceOptions _options;

    public KeyedBinanceLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<BinanceOptions> options, ILogger<KeyedBinanceLoginFormViewModel> logger,
          IBrokerCredentialVerifier verifier)
        : base(selector, credentials, logger, verifier)
    {
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Binance;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "Binance";

    public override bool UsesPassphrase => false;

    public override bool UsesPrivateKeyPem => false;
}
