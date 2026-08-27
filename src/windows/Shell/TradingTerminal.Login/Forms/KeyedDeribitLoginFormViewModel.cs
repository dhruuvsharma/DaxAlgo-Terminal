using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to Deribit — the second of its two login rows.
///
/// <para>Higher rate limits and the private channels. Market data itself stays public.</para>
/// </summary>
public sealed class KeyedDeribitLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly DeribitOptions _options;

    public KeyedDeribitLoginFormViewModel(
        IBrokerSelector selector, CredentialStore credentials,
        IOptions<DeribitOptions> options, ILogger<KeyedDeribitLoginFormViewModel> logger,
          IBrokerCredentialVerifier verifier)
        : base(selector, credentials, logger, verifier) => _options = options.Value;

    public override BrokerKind Broker => BrokerKind.Deribit;

    protected override CryptoApiCredentials Target => _options.Credentials;

    protected override string VenueName => "Deribit";

    // Not an HMAC key pair: Deribit issues OAuth2 client credentials and exchanges them for a token,
    // so the field labels a user looks for on the API page are different words entirely.
    protected override string CredentialShape => "Client ID + client secret (OAuth2)";

    protected override string WhatAKeyBuys => "private channels, options + perps positions";
}
