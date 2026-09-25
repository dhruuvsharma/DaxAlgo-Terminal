using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to Coinbase — the second of its two login rows.
///
/// <para>The secret is an EC private key in PEM — Coinbase signs an ES256 JWT, not an HMAC.</para>
/// </summary>
public sealed class KeyedCoinbaseLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly CoinbaseOptions _options;

    public KeyedCoinbaseLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<CoinbaseOptions> options, ILogger<KeyedCoinbaseLoginFormViewModel> logger,
          IBrokerCredentialVerifier verifier)
        : base(selector, credentials, logger, verifier)
    {
        _options = options.Value;
    }

    public override BrokerKind Broker => BrokerKind.Coinbase;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "Coinbase";

    public override bool UsesPassphrase => false;

    public override bool UsesPrivateKeyPem => true;

    // The "secret" is an EC private key in PEM, used to mint an ES256 JWT per request — pasting a
    // Coinbase key name where the PEM belongs fails in a way nothing explains, so the row says so.
    protected override string WhatAKeyBuys => "Advanced Trade accounts, private WebSocket";

    public override string KeyLabel => "API key name";

    public override string SecretLabel => "EC private key (PEM)";

    // ECDSA is the one to name: this row signs an ES256 JWT, and a key made with the other signature
    // algorithm the portal offers cannot sign one.
    public override string WhereToGetAKey =>
        "Create a secret API key at cdp.coinbase.com with the ECDSA signature algorithm and View "
        + "permission. The key name looks like organizations/…/apiKeys/…; paste the whole private "
        + "key, BEGIN and END lines included — copied straight from the downloaded JSON is fine.";
}
