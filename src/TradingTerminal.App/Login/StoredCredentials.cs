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
