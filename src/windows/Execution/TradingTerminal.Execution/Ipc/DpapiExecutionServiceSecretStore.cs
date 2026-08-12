using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Execution.Ipc;

/// <summary>Loads or creates the per-service secret shared by the current user's local processes.</summary>
public interface IExecutionServiceSecretStore
{
    /// <summary>Returns exactly 32 unprotected secret bytes.</summary>
    byte[] LoadOrCreate();
}

/// <summary>
/// Stores one randomly generated service secret protected by Windows DPAPI for the current user.
/// Corrupt existing state fails closed; it is never silently replaced with a new credential.
/// </summary>
public sealed class DpapiExecutionServiceSecretStore : IExecutionServiceSecretStore
{
    /// <summary>Required unprotected secret size.</summary>
    public const int SecretSize = 32;

    private const int MaximumProtectedSecretBytes = 16 * 1024;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("DaxAlgo.Terminal.Execution.Service.Secret.v1");
    private readonly object _gate = new();

    /// <summary>Creates a store at the dedicated default current-user local-app-data path.</summary>
    public DpapiExecutionServiceSecretStore()
        : this(DefaultSecretPath)
    {
    }

    /// <summary>Creates a store at an injected path, primarily for deterministic isolation in tests.</summary>
    public DpapiExecutionServiceSecretStore(string secretPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretPath);
        SecretPath = Path.GetFullPath(secretPath);
    }

    /// <summary>Default DPAPI-protected credential path.</summary>
    public static string DefaultSecretPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "Execution",
        "service-secret.dpapi");

    /// <summary>Gets the normalized backing-file path.</summary>
    public string SecretPath { get; }

    /// <inheritdoc />
    public byte[] LoadOrCreate()
    {
        lock (_gate)
        {
            if (File.Exists(SecretPath))
                return ReadExisting();

            var directory = Path.GetDirectoryName(SecretPath) ??
                throw new InvalidOperationException("The service-secret path has no parent directory.");
            Directory.CreateDirectory(directory);

            var secret = RandomNumberGenerator.GetBytes(SecretSize);
            byte[]? protectedSecret = null;
            try
            {
                protectedSecret = ProtectedData.Protect(
                    secret,
                    OptionalEntropy,
                    DataProtectionScope.CurrentUser);
                try
                {
                    using var file = new FileStream(
                        SecretPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.WriteThrough);
                    file.Write(protectedSecret);
                    file.Flush(flushToDisk: true);
                    return (byte[])secret.Clone();
                }
                catch (IOException) when (File.Exists(SecretPath))
                {
                    // A concurrent first-run creator won. Its committed credential is authoritative.
                    return ReadExisting();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
                if (protectedSecret is not null)
                    CryptographicOperations.ZeroMemory(protectedSecret);
            }
        }
    }

    private byte[] ReadExisting()
    {
        var fileInfo = new FileInfo(SecretPath);
        if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaximumProtectedSecretBytes)
            throw new InvalidDataException("The DPAPI-protected execution service secret is missing or invalid.");

        var protectedSecret = File.ReadAllBytes(SecretPath);
        byte[]? secret = null;
        try
        {
            secret = ProtectedData.Unprotect(
                protectedSecret,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (secret.Length != SecretSize)
                throw new InvalidDataException("The unprotected execution service secret has an invalid length.");
            return (byte[])secret.Clone();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedSecret);
            if (secret is not null)
                CryptographicOperations.ZeroMemory(secret);
        }
    }
}
