using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Accounts;

namespace TradingTerminal.Accounts;

internal interface IDevelopmentAccountSessionStore
{
    AccountSessionSnapshot? Load();

    bool Save(AccountSessionSnapshot session);

    bool Clear();
}

internal interface IAccountSessionProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

internal sealed class DevelopmentAccountSessionStore(
    string filePath,
    IAccountSessionProtector protector)
    : IDevelopmentAccountSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };
    private readonly object _gate = new();

    public static DevelopmentAccountSessionStore CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        return new DevelopmentAccountSessionStore(
            Path.Combine(directory, "account-session.dat"),
            DpapiAccountSessionProtector.Instance);
    }

    public AccountSessionSnapshot? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var ciphertext = File.ReadAllBytes(filePath);
                byte[]? plaintext = null;
                try
                {
                    plaintext = protector.Unprotect(ciphertext);
                    var stored = JsonSerializer.Deserialize<StoredAccountSession>(
                        plaintext,
                        JsonOptions);
                    if (stored is null) return null;
                    return new AccountSessionSnapshot(
                        stored.SessionId,
                        new AccountIdentity(
                            stored.AccountId,
                            stored.DisplayName,
                            stored.EmailAddress),
                        stored.AuthenticatedAtUtc,
                        stored.ExpiresAtUtc);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                    if (plaintext is not null)
                        CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch
            {
                TryDelete();
                return null;
            }
        }
    }

    public bool Save(AccountSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var stored = new StoredAccountSession(
                session.SessionId,
                session.Account.AccountId,
                session.Account.DisplayName,
                session.Account.EmailAddress,
                session.AuthenticatedAtUtc,
                session.ExpiresAtUtc);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions);
            byte[]? ciphertext = null;
            string? temporaryPath = null;
            try
            {
                ciphertext = protector.Protect(plaintext);
                var directory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(directory)) return false;
                Directory.CreateDirectory(directory);
                temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, ciphertext);
                File.Move(temporaryPath, filePath, overwrite: true);
                temporaryPath = null;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext is not null)
                    CryptographicOperations.ZeroMemory(ciphertext);
                if (temporaryPath is not null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (!File.Exists(filePath)) return true;
            return TryDelete();
        }
    }

    private bool TryDelete()
    {
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record StoredAccountSession(
        string SessionId,
        string AccountId,
        string? DisplayName,
        string? EmailAddress,
        DateTimeOffset AuthenticatedAtUtc,
        DateTimeOffset? ExpiresAtUtc);
}

internal sealed class DpapiAccountSessionProtector : IAccountSessionProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DaxAlgoTerminal.AccountGate.Session.v1");

    public static DpapiAccountSessionProtector Instance { get; } = new();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(
            plaintext,
            Entropy,
            DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(
            ciphertext,
            Entropy,
            DataProtectionScope.CurrentUser);
}
