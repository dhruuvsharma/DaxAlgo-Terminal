using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>
/// On-disk shape for persisted connection settings. The password (when remembered)
/// is DPAPI-encrypted under <see cref="DataProtectionScope.CurrentUser"/>, so it can
/// only be decrypted by the same Windows user on the same machine.
/// </summary>
public sealed class StoredCredentials
{
    /// <summary>Which broker the user last signed in with. Drives the form shown on next launch.</summary>
    public BrokerKind SelectedBroker { get; set; } = BrokerKind.InteractiveBrokers;

    /// <summary>When true, the login window fires every available broker's Connect on startup
    /// (each form using its own persisted credentials) instead of waiting for manual clicks.</summary>
    public bool AutoConnect { get; set; }

    public string? Username { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 1;
    public string AccountType { get; set; } = "Paper";
    public int MarketDataType { get; set; } = 1;
    public bool RememberPassword { get; set; }

    // ---- NinjaTrader-specific fields ----
    public string NinjaAccountName { get; set; } = "Sim101";
    public string NinjaDllPath { get; set; } = string.Empty;
    public string NinjaFuturesContractMonth { get; set; } = string.Empty;

    // ---- cTrader-specific fields ----
    public string CTraderClientId { get; set; } = string.Empty;
    public long CTraderAccountId { get; set; }
    public bool CTraderIsLive { get; set; }

    /// <summary>Base64-encoded DPAPI ciphertext for the OAuth client secret.</summary>
    public string? CTraderClientSecretEncryptedBase64 { get; set; }
    /// <summary>Base64-encoded DPAPI ciphertext for the OAuth access token.</summary>
    public string? CTraderAccessTokenEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? CTraderClientSecret
    {
        get => DecryptDpapi(CTraderClientSecretEncryptedBase64);
        set => CTraderClientSecretEncryptedBase64 = EncryptDpapi(value);
    }

    [JsonIgnore]
    public string? CTraderAccessToken
    {
        get => DecryptDpapi(CTraderAccessTokenEncryptedBase64);
        set => CTraderAccessTokenEncryptedBase64 = EncryptDpapi(value);
    }

    // ---- IronBeam-specific fields ----
    public string? IronBeamUsername { get; set; }
    public bool IronBeamIsLive { get; set; }

    /// <summary>Base64-encoded DPAPI ciphertext for the IronBeam API key.</summary>
    public string? IronBeamApiKeyEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? IronBeamApiKey
    {
        get => DecryptDpapi(IronBeamApiKeyEncryptedBase64);
        set => IronBeamApiKeyEncryptedBase64 = EncryptDpapi(value);
    }

    // ---- London Strategic Edge-specific fields ----

    /// <summary>Base64-encoded DPAPI ciphertext for the London Strategic Edge API key.</summary>
    public string? LondonStrategicEdgeApiKeyEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? LondonStrategicEdgeApiKey
    {
        get => DecryptDpapi(LondonStrategicEdgeApiKeyEncryptedBase64);
        set => LondonStrategicEdgeApiKeyEncryptedBase64 = EncryptDpapi(value);
    }

    // ---- Alpaca-specific fields ----
    public string AlpacaApiKey { get; set; } = string.Empty;
    public bool AlpacaIsLive { get; set; }
    public string AlpacaStockDataFeed { get; set; } = "iex";

    /// <summary>Base64-encoded DPAPI ciphertext for the Alpaca API secret.</summary>
    public string? AlpacaApiSecretEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? AlpacaApiSecret
    {
        get => DecryptDpapi(AlpacaApiSecretEncryptedBase64);
        set => AlpacaApiSecretEncryptedBase64 = EncryptDpapi(value);
    }

    // ---- Per-broker API credentials ----

    /// <summary>
    /// API credentials for any broker that authenticates with a key, secret and optional passphrase,
    /// stored under the broker's name.
    ///
    /// <para>A map rather than three named fields per venue, because there are five of them today and
    /// the shape is identical — fifteen near-duplicate properties would be a lot of surface for no
    /// information, and each new venue would mean touching this file again.</para>
    ///
    /// <para>Secrets are DPAPI ciphertext, exactly like the named fields above; the plaintext never
    /// reaches this object's serialised form. The key name is stored in the clear on purpose — it is an
    /// identifier, not a secret, and having it readable is what lets the login form show which key is
    /// configured without decrypting anything.</para>
    /// </summary>
    public Dictionary<string, BrokerKeyRecord> BrokerKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a venue's credentials, or an empty record when none are stored.</summary>
    public BrokerKeyRecord KeysFor(BrokerKind broker) =>
        BrokerKeys.TryGetValue(broker.ToString(), out var record) ? record : new BrokerKeyRecord();

    /// <summary>Writes a venue's credentials, replacing whatever was there.</summary>
    public void SetKeys(BrokerKind broker, string apiKey, string? secret, string? passphrase)
    {
        BrokerKeys[broker.ToString()] = new BrokerKeyRecord
        {
            ApiKey = apiKey ?? string.Empty,
            ApiSecret = secret,
            Passphrase = passphrase,
        };
    }

    /// <summary>Forgets a venue's credentials — what choosing the keyless row does, so "keyless"
    /// means keyless rather than "authenticated because you once pasted a key".</summary>
    public void ClearKeys(BrokerKind broker) => BrokerKeys.Remove(broker.ToString());

    // ---- Upstox-specific fields ----
    public string UpstoxApiKey { get; set; } = string.Empty;
    public string UpstoxRedirectUri { get; set; } = string.Empty;

    /// <summary>Base64-encoded DPAPI ciphertext for the Upstox OAuth client secret.</summary>
    public string? UpstoxApiSecretEncryptedBase64 { get; set; }

    /// <summary>Base64-encoded DPAPI ciphertext for the Upstox access token (expires daily ~03:30 IST).</summary>
    public string? UpstoxAccessTokenEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? UpstoxApiSecret
    {
        get => DecryptDpapi(UpstoxApiSecretEncryptedBase64);
        set => UpstoxApiSecretEncryptedBase64 = EncryptDpapi(value);
    }

    [JsonIgnore]
    public string? UpstoxAccessToken
    {
        get => DecryptDpapi(UpstoxAccessTokenEncryptedBase64);
        set => UpstoxAccessTokenEncryptedBase64 = EncryptDpapi(value);
    }

    internal static string? DecryptDpapi(string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(encryptedBase64);
            var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    internal static string? EncryptDpapi(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Base64-encoded DPAPI ciphertext. Null when password is not remembered.</summary>
    public string? PasswordEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? Password
    {
        get
        {
            if (string.IsNullOrEmpty(PasswordEncryptedBase64)) return null;
            try
            {
                var bytes = Convert.FromBase64String(PasswordEncryptedBase64);
                var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null,
                    DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                PasswordEncryptedBase64 = null;
                return;
            }
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            PasswordEncryptedBase64 = Convert.ToBase64String(encrypted);
        }
    }
}
/// <summary>One broker's stored API credentials. Secrets are DPAPI ciphertext at rest.</summary>
public sealed class BrokerKeyRecord
{
    /// <summary>The API key, or the CDP key name for Coinbase. An identifier, not a secret, so it is
    /// stored in the clear — which is what lets a form show which key is configured without
    /// decrypting.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>DPAPI ciphertext for the secret — or, for Coinbase, for the EC private key.</summary>
    public string? ApiSecretEncryptedBase64 { get; set; }

    /// <summary>DPAPI ciphertext for the passphrase. Only OKX issues one.</summary>
    public string? PassphraseEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? ApiSecret
    {
        get => StoredCredentials.DecryptDpapi(ApiSecretEncryptedBase64);
        set => ApiSecretEncryptedBase64 = StoredCredentials.EncryptDpapi(value);
    }

    [JsonIgnore]
    public string? Passphrase
    {
        get => StoredCredentials.DecryptDpapi(PassphraseEncryptedBase64);
        set => PassphraseEncryptedBase64 = StoredCredentials.EncryptDpapi(value);
    }
}
