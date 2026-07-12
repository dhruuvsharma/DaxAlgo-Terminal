using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Login;

/// <summary>
/// Persists AI-provider API keys, one per provider id, DPAPI-encrypted under the current Windows user
/// (same scheme as the broker credential store) in <c>%LocalAppData%/DaxAlgoTerminal/ai-keys.json</c>.
/// Kept separate from the broker <see cref="CredentialStore"/> so AI setup and trading credentials
/// don't share a file. Read by the codegen key resolver; written by the AI-providers settings section.
/// </summary>
public sealed class AiKeyStore : IAiKeyStore
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgoTerminal");
    private static readonly string FilePath = Path.Combine(Directory, "ai-keys.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<AiKeyStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string> _encrypted; // providerId -> DPAPI base64

    public AiKeyStore(ILogger<AiKeyStore> logger)
    {
        _logger = logger;
        _encrypted = Load();
    }

    /// <summary>Provider ids that currently have a stored key.</summary>
    public IReadOnlyCollection<string> ConfiguredProviders
    {
        get { lock (_gate) return _encrypted.Keys.ToArray(); }
    }

    public bool HasKey(string providerId)
    {
        lock (_gate) return _encrypted.ContainsKey(providerId);
    }

    /// <summary>The decrypted key for <paramref name="providerId"/>, or null when none is stored (or it
    /// can't be decrypted — a machine/user change).</summary>
    public string? Get(string providerId)
    {
        lock (_gate)
            return _encrypted.TryGetValue(providerId, out var enc) ? Decrypt(enc) : null;
    }

    public void Set(string providerId, string apiKey)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) { Remove(providerId); return; }
            _encrypted[providerId] = Encrypt(apiKey);
            Save();
        }
    }

    public void Remove(string providerId)
    {
        lock (_gate)
        {
            if (_encrypted.Remove(providerId)) Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath), JsonOptions)
                is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read AI key store; starting fresh");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_encrypted, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist AI keys");
        }
    }

    private static string Encrypt(string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private string? Decrypt(string encryptedBase64)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt a stored AI key (machine/user change?)");
            return null;
        }
    }
}
