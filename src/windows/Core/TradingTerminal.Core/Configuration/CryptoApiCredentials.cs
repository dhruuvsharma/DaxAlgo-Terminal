namespace TradingTerminal.Core.Configuration;

/// <summary>
/// The API credentials for one crypto venue, when the user has chosen the keyed way in.
///
/// <para>One type for all five because the shape barely differs — a key, a secret, and for two of them
/// a third field. Coinbase is the odd one: its "secret" is an EC private key in PEM rather than a
/// shared secret, because its scheme is an ES256 JWT rather than an HMAC. Same slot, different
/// contents, and the venue's signer knows which it is.</para>
///
/// <para><b>Empty is the normal state and means keyless.</b> All five venues serve quotes, books and
/// candles with no credentials at all, so an absent key is not a misconfiguration — it is the other
/// supported mode, and the client behaves accordingly rather than refusing to start.</para>
///
/// <para>These are populated at runtime from the DPAPI credential store by the login form, never from
/// <c>appsettings.json</c>. Nothing here should ever be committed to a config file.</para>
/// </summary>
public sealed class CryptoApiCredentials
{
    /// <summary>The API key, or the CDP key name for Coinbase.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The shared secret — or, for Coinbase, the EC private key in PEM.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>The passphrase chosen when the key was created. OKX requires one; it is not the
    /// account password, and it cannot be recovered — only replaced by issuing a new key.</summary>
    public string Passphrase { get; set; } = string.Empty;

    /// <summary>True when there is enough here to sign with.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    /// <summary>Forgets everything — what the keyless row does, so choosing it actually drops the key
    /// rather than leaving the connection quietly authenticated.</summary>
    public void Clear()
    {
        ApiKey = string.Empty;
        ApiSecret = string.Empty;
        Passphrase = string.Empty;
    }
}
