using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Execution.Oms;

/// <summary>The execution environment selected for one broker connection.</summary>
public enum ExecutionMode : byte
{
    /// <summary>Routes only to a broker's simulation, demo, testnet, or paper endpoint.</summary>
    Paper = 0,

    /// <summary>Routes to a real-money broker endpoint after explicit authorization.</summary>
    Live = 1,
}

/// <summary>
/// Persisted evidence that one identity deliberately acknowledged live execution for one exact
/// mode-neutral broker/account binding.
/// </summary>
public sealed record LiveExecutionConfirmation(
    string BrokerId,
    string AccountId,
    string Acknowledgement,
    DateTime ConfirmedAtUtc,
    string ConfirmedBy)
{
    public const string RequiredAcknowledgement = "LIVE";
    public const int MaximumBrokerIdLength = 64;
    public const int MaximumAccountIdLength = 128;
    public const int MaximumConfirmingIdentityLength = 256;

    /// <summary>Gets whether every field is exact, bounded, and suitable for live authorization.</summary>
    public bool IsValid =>
        IsBounded(BrokerId, MaximumBrokerIdLength) &&
        IsBounded(AccountId, MaximumAccountIdLength) &&
        string.Equals(Acknowledgement, RequiredAcknowledgement, StringComparison.Ordinal) &&
        ConfirmedAtUtc != default &&
        ConfirmedAtUtc.Kind == DateTimeKind.Utc &&
        IsBounded(ConfirmedBy, MaximumConfirmingIdentityLength);

    /// <summary>Gets whether this confirmation authorizes the exact broker/account binding.</summary>
    public bool Matches(string brokerId, string accountId) =>
        IsValid &&
        string.Equals(BrokerId, brokerId, StringComparison.Ordinal) &&
        string.Equals(AccountId, accountId, StringComparison.Ordinal);

    internal static bool IsLookupValid(string brokerId, string accountId) =>
        IsBounded(brokerId, MaximumBrokerIdLength) &&
        IsBounded(accountId, MaximumAccountIdLength);

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}

/// <summary>Reads and persists explicit live-execution confirmations.</summary>
public interface ILiveExecutionConfirmationStore
{
    /// <summary>Reads the confirmation for one exact mode-neutral broker/account binding.</summary>
    LiveExecutionConfirmation? Read(string brokerId, string accountId);

    /// <summary>Persists one validated confirmation, replacing only the same broker/account binding.</summary>
    void Save(LiveExecutionConfirmation confirmation);

    /// <summary>Revokes the confirmation for one exact broker/account binding.</summary>
    bool Remove(string brokerId, string accountId);
}

/// <summary>Bounded deterministic confirmation store for tests and explicitly ephemeral hosts.</summary>
public sealed class InMemoryLiveExecutionConfirmationStore : ILiveExecutionConfirmationStore
{
    public const int MaximumConfirmations = 64;

    private readonly object _gate = new();
    private readonly Dictionary<(string BrokerId, string AccountId), LiveExecutionConfirmation> _confirmations = [];

    /// <inheritdoc />
    public LiveExecutionConfirmation? Read(string brokerId, string accountId)
    {
        ValidateLookup(brokerId, accountId);
        lock (_gate)
            return _confirmations.GetValueOrDefault((brokerId, accountId));
    }

    /// <inheritdoc />
    public void Save(LiveExecutionConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.IsValid)
            throw new ArgumentException("The live-execution confirmation is invalid.", nameof(confirmation));

        lock (_gate)
        {
            var key = (confirmation.BrokerId, confirmation.AccountId);
            if (!_confirmations.ContainsKey(key) && _confirmations.Count >= MaximumConfirmations)
                throw new InvalidOperationException($"No more than {MaximumConfirmations} live confirmations may be retained.");
            _confirmations[key] = confirmation;
        }
    }

    /// <inheritdoc />
    public bool Remove(string brokerId, string accountId)
    {
        ValidateLookup(brokerId, accountId);
        lock (_gate)
            return _confirmations.Remove((brokerId, accountId));
    }

    internal static void ValidateLookup(string brokerId, string accountId)
    {
        if (!LiveExecutionConfirmation.IsLookupValid(brokerId, accountId))
            throw new ArgumentException("The live-confirmation broker/account lookup is invalid.");
    }
}

/// <summary>
/// Bounded current-user DPAPI store for explicit live confirmations. Corrupt or oversized persisted
/// state fails closed and is never silently replaced.
/// </summary>
public sealed class DpapiLiveExecutionConfirmationStore : ILiveExecutionConfirmationStore
{
    private const int DocumentVersion = 1;
    private const int MaximumPlaintextBytes = 128 * 1024;
    private const int MaximumProtectedBytes = 256 * 1024;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("DaxAlgo.Terminal.Execution.LiveConfirmations.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly object _gate = new();

    /// <summary>Creates a store at the dedicated current-user local-app-data path.</summary>
    public DpapiLiveExecutionConfirmationStore()
        : this(DefaultConfirmationPath)
    {
    }

    /// <summary>Creates a store at an injected path, primarily for deterministic test isolation.</summary>
    public DpapiLiveExecutionConfirmationStore(string confirmationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationPath);
        ConfirmationPath = Path.GetFullPath(confirmationPath);
    }

    /// <summary>Default current-user DPAPI confirmation path.</summary>
    public static string DefaultConfirmationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "Execution",
        "live-confirmations.dpapi");

    /// <summary>Gets the normalized backing-file path.</summary>
    public string ConfirmationPath { get; }

    /// <inheritdoc />
    public LiveExecutionConfirmation? Read(string brokerId, string accountId)
    {
        InMemoryLiveExecutionConfirmationStore.ValidateLookup(brokerId, accountId);
        lock (_gate)
            return ReadAll().SingleOrDefault(item => item.Matches(brokerId, accountId));
    }

    /// <inheritdoc />
    public void Save(LiveExecutionConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!confirmation.IsValid)
            throw new ArgumentException("The live-execution confirmation is invalid.", nameof(confirmation));

        lock (_gate)
        {
            var confirmations = ReadAll().ToList();
            var existing = confirmations.FindIndex(item =>
                string.Equals(item.BrokerId, confirmation.BrokerId, StringComparison.Ordinal) &&
                string.Equals(item.AccountId, confirmation.AccountId, StringComparison.Ordinal));
            if (existing >= 0)
            {
                confirmations[existing] = confirmation;
            }
            else
            {
                if (confirmations.Count >= InMemoryLiveExecutionConfirmationStore.MaximumConfirmations)
                {
                    throw new InvalidOperationException(
                        $"No more than {InMemoryLiveExecutionConfirmationStore.MaximumConfirmations} live confirmations may be retained.");
                }
                confirmations.Add(confirmation);
            }

            WriteAll(confirmations);
        }
    }

    /// <inheritdoc />
    public bool Remove(string brokerId, string accountId)
    {
        InMemoryLiveExecutionConfirmationStore.ValidateLookup(brokerId, accountId);
        lock (_gate)
        {
            var confirmations = ReadAll().ToList();
            var removed = confirmations.RemoveAll(item =>
                string.Equals(item.BrokerId, brokerId, StringComparison.Ordinal) &&
                string.Equals(item.AccountId, accountId, StringComparison.Ordinal)) != 0;
            if (removed)
                WriteAll(confirmations);
            return removed;
        }
    }

    private IReadOnlyList<LiveExecutionConfirmation> ReadAll()
    {
        if (!File.Exists(ConfirmationPath))
            return Array.Empty<LiveExecutionConfirmation>();

        var info = new FileInfo(ConfirmationPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumProtectedBytes)
            throw new InvalidDataException("The DPAPI-protected live-confirmation file is missing, empty, or oversized.");

        var protectedBytes = File.ReadAllBytes(ConfirmationPath);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(protectedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length <= 0 || plaintext.Length > MaximumPlaintextBytes)
                throw new InvalidDataException("The unprotected live-confirmation document is empty or oversized.");

            ConfirmationDocument document;
            try
            {
                document = JsonSerializer.Deserialize<ConfirmationDocument>(plaintext, JsonOptions) ??
                    throw new InvalidDataException("The live-confirmation document is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The live-confirmation document is malformed.", exception);
            }

            if (document.Version != DocumentVersion ||
                document.Confirmations is null ||
                document.Confirmations.Count > InMemoryLiveExecutionConfirmationStore.MaximumConfirmations ||
                document.Confirmations.Any(item => item is null || !item.IsValid))
            {
                throw new InvalidDataException("The live-confirmation document contains invalid or unsupported state.");
            }

            var duplicates = document.Confirmations
                .GroupBy(item => (item.BrokerId, item.AccountId))
                .Any(group => group.Count() != 1);
            if (duplicates)
                throw new InvalidDataException("The live-confirmation document contains duplicate broker/account bindings.");

            return Array.AsReadOnly(document.Confirmations.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void WriteAll(IReadOnlyList<LiveExecutionConfirmation> confirmations)
    {
        var document = new ConfirmationDocument(DocumentVersion, confirmations.ToArray());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (plaintext.Length <= 0 || plaintext.Length > MaximumPlaintextBytes)
            throw new InvalidOperationException("The bounded live-confirmation document cannot be persisted safely.");

        byte[]? protectedBytes = null;
        var temporaryPath = ConfirmationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, OptionalEntropy, DataProtectionScope.CurrentUser);
            if (protectedBytes.Length <= 0 || protectedBytes.Length > MaximumProtectedBytes)
                throw new InvalidOperationException("The protected live-confirmation document is oversized.");

            var directory = Path.GetDirectoryName(ConfirmationPath) ??
                throw new InvalidOperationException("The live-confirmation path has no parent directory.");
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                file.Write(protectedBytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, ConfirmationPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record ConfirmationDocument(
        int Version,
        IReadOnlyList<LiveExecutionConfirmation> Confirmations);
}

/// <summary>Shared fail-closed validation used before any live endpoint token is created.</summary>
internal static class LiveExecutionAuthorizationGate
{
    internal static LiveExecutionConfirmation Require(
        bool allowLiveExecution,
        bool hasRequiredCredentials,
        string brokerId,
        string accountId,
        ILiveExecutionConfirmationStore? confirmationStore)
    {
        if (!allowLiveExecution)
            throw new InvalidOperationException("Live execution is disabled because AllowLiveExecution defaults to false.");
        if (!hasRequiredCredentials)
            throw new InvalidOperationException("Live execution requires real, non-paper credentials before an endpoint can be created.");
        if (!LiveExecutionConfirmation.IsLookupValid(brokerId, accountId))
            throw new InvalidOperationException("Live execution requires one exact bounded broker/account binding.");
        if (confirmationStore is null)
            throw new InvalidOperationException("Live execution requires a persisted typed confirmation store.");

        var confirmation = confirmationStore.Read(brokerId, accountId);
        if (confirmation is null || !confirmation.Matches(brokerId, accountId))
        {
            throw new InvalidOperationException(
                $"Live execution for broker '{brokerId}' account '{accountId}' requires a persisted exact LIVE confirmation.");
        }
        return confirmation;
    }
}
