using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// The keyed way in to a crypto venue that also serves data publicly.
///
/// <para>Five venues appear twice in the login list on purpose: once under <b>Keyless</b>, where they
/// connect immediately with no account, and once here under <b>Key required</b>. Both rows drive the
/// same client and the same <see cref="BrokerKind"/> — because it is the same venue and the same
/// market, and splitting the provenance would split the stored history of one exchange across two
/// partitions. What differs is whether credentials are handed over before connecting.</para>
///
/// <para><b>What a key actually buys, stated plainly:</b> a higher rate-limit budget, and the private
/// endpoints that order routing will need later. It does <i>not</i> unlock market data — quotes, books
/// and candles are public at all five venues, and a user who never wants an account loses nothing by
/// staying in the keyless group.</para>
///
/// <para>Choosing this row and connecting replaces whatever the keyless row left behind; choosing the
/// keyless row clears the credentials, so "keyless" means keyless rather than "authenticated because
/// you once pasted a key".</para>
/// </summary>
public abstract class KeyedCryptoLoginFormBase : BrokerLoginFormBase
{
    private readonly CredentialStore _credentials;
    private readonly IBrokerCredentialVerifier _verifier;

    protected KeyedCryptoLoginFormBase(
        IBrokerSelector selector, CredentialStore credentials, ILogger logger,
        IBrokerCredentialVerifier? verifier = null)
        : base(selector, logger)
    {
        _credentials = credentials;
        _verifier = verifier ?? IBrokerCredentialVerifier.None;
    }

    /// <summary>The venue's options slot, so a key entered here is visible to anything reading
    /// <c>IOptions</c>. The authenticated path itself goes through the DPAPI store and
    /// <c>IBrokerCredentialSource</c>; this slot is the in-memory mirror for the current session.</summary>
    protected abstract CryptoApiCredentials Target { get; }

    /// <summary>The venue's own name, without the "(API key)" suffix this row adds.</summary>
    protected abstract string VenueName { get; }

    /// <summary>True when this venue's keys carry a passphrase. Only OKX does, and showing the field
    /// for the others would be asking for something that does not exist.</summary>
    public virtual bool UsesPassphrase => false;

    /// <summary>True when the secret is an EC private key in PEM rather than a shared secret — only
    /// Coinbase, whose scheme is an ES256 JWT. Worth saying in the form, because pasting a Coinbase
    /// key name where a PEM belongs fails in a way nothing explains.</summary>
    public virtual bool UsesPrivateKeyPem => false;

    /// <summary>This row always sits in the keyed group, whatever the venue's keyless tile says.</summary>
    public override LoginCategory Category => LoginCategory.Credentialed;

    public override string DisplayName => $"{VenueName} (API key)";

    /// <summary>
    /// What this row wants and what it buys — never the keyless row's description.
    ///
    /// <para>The tile table is keyed by <see cref="BrokerKind"/>, which both rows share, so the default
    /// would say "Public WebSocket · live crypto, L2 depth" here: a description of the row *above* this
    /// one, sitting under a heading that promises a key is required. Composed from the credential shape
    /// each venue actually uses, so the row states what to go and get before the user opens it.</para>
    /// </summary>
    public override string Subtitle => $"{CredentialShape} · {WhatAKeyBuys}";

    /// <summary>The fields this venue's form asks for, named the way the venue names them.</summary>
    protected virtual string CredentialShape =>
        UsesPrivateKeyPem ? "Key name + EC private key (PEM)"
        : UsesPassphrase ? "API key + secret + passphrase"
        : "API key + secret";

    /// <summary>What the key adds over the keyless row. Overridden where a venue offers more.</summary>
    protected virtual string WhatAKeyBuys => "private endpoints, higher rate limits";

    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) RaiseCanSubmit(); }
    }

    private string _apiSecret = string.Empty;
    public string ApiSecret
    {
        get => _apiSecret;
        set { if (SetProperty(ref _apiSecret, value)) RaiseCanSubmit(); }
    }

    private string _passphrase = string.Empty;
    public string Passphrase
    {
        get => _passphrase;
        set { if (SetProperty(ref _passphrase, value)) RaiseCanSubmit(); }
    }

    public override bool CanSubmit =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && (!UsesPassphrase || !string.IsNullOrWhiteSpace(Passphrase));

    public override void ApplyToOptions()
    {
        Target.ApiKey = ApiKey.Trim();
        Target.ApiSecret = ApiSecret.Trim();
        Target.Passphrase = UsesPassphrase ? Passphrase.Trim() : string.Empty;
    }

    public override string GetSessionAccountLabel() => $"{VenueName} · API key";

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out reaching {VenueName}. Check your internet connection.";

    public override string GetFailureMessage() =>
        $"{VenueName} rejected the connection. A signature that is wrong in any detail is refused "
        + "exactly like a bad key, so check the secret, and the passphrase if this venue uses one.";

    /// <summary>
    /// Makes one signed, read-only balance call before connecting.
    ///
    /// <para>This is the whole reason the keyed row is worth choosing over the keyless one at the
    /// moment of setup: market data is public at these venues, so nothing else in the connect path
    /// touches the key, and a wrong secret would produce a perfectly successful login.</para>
    ///
    /// <para>Only a <i>refusal</i> stops the login. If the venue could not be reached, the connection
    /// proceeds — an unreachable API host and an invalid key are different problems, and reporting the
    /// first as the second sends a user off to regenerate a key that was always fine.</para>
    /// </summary>
    protected override async Task<string?> VerifyCredentialsAsync(CancellationToken ct)
    {
        if (!_verifier.CanVerify(Broker)) return null;

        var credential = new BrokerCredential(
            Key: ApiKey.Trim(),
            Secret: ApiSecret.Trim(),
            Passphrase: UsesPassphrase ? Passphrase.Trim() : string.Empty);

        var verification = await _verifier.VerifyAsync(Broker, credential, ct).ConfigureAwait(true);

        if (!verification.IsRefusal) return null;

        return $"{VenueName} rejected these credentials: {verification.Detail} "
            + "The keyless row connects to the same market data without an account.";
    }

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void Load()
    {
        var record = _credentials.Load().KeysFor(Broker);
        ApiKey = record.ApiKey;
        ApiSecret = record.ApiSecret ?? string.Empty;
        Passphrase = record.Passphrase ?? string.Empty;
    }

    public override void Save()
    {
        // Through the same DPAPI store as every other broker secret. Nothing reaches
        // appsettings.json, which is plain text sitting in the user's profile.
        var stored = _credentials.Load();
        stored.SelectedBroker = Broker;
        stored.SetKeys(
            Broker,
            ApiKey.Trim(),
            string.IsNullOrEmpty(ApiSecret) ? null : ApiSecret,
            UsesPassphrase && !string.IsNullOrEmpty(Passphrase) ? Passphrase : null);
        _credentials.Save(stored);
    }
}
