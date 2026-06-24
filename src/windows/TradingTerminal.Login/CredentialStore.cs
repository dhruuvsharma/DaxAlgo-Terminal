using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.App.Login;

public sealed class CredentialStore
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal");

    private static readonly string FilePath = Path.Combine(Directory, "connection.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<CredentialStore> _logger;

    public CredentialStore(ILogger<CredentialStore> logger) => _logger = logger;

    public StoredCredentials Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new StoredCredentials();
            var json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<StoredCredentials>(json, JsonOptions);
            return parsed ?? new StoredCredentials();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read credential store; starting fresh");
            return new StoredCredentials();
        }
    }

    public void Save(StoredCredentials credentials)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(credentials, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist credentials");
        }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to clear credential store"); }
    }
}
