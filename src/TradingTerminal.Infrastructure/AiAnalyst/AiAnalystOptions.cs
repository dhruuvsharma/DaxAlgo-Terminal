using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.AiAnalyst;

/// <summary>
/// Configuration for the AI Market Analyst feature. Lives under
/// <c>Notifications:AiAnalyst</c> in <c>notifications.json</c>, alongside Telegram /
/// Discord / Ollama. The API key is DPAPI-encrypted on disk under
/// <see cref="DataProtectionScope.CurrentUser"/> — never in <c>appsettings.json</c>.
/// </summary>
public sealed class AiAnalystOptions
{
    /// <summary>True when the user has wired up the Python sidecar. When false the DI
    /// registration installs <c>NullAiAnalystClient</c> and the UI shows "unavailable".</summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback URL of the FastAPI sidecar. The default port matches the
    /// PyInstaller launcher's default; override only when running a custom dev server.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8765";

    /// <summary>Which LLM family to route through: <c>openai</c>, <c>anthropic</c>,
    /// <c>qwen</c>, or <c>minimax</c>. The Python side maps this to a provider client.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Text/reasoning model id (e.g. <c>gpt-4o</c>, <c>claude-opus-4-5</c>).</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>Vision-capable model id used by the pattern + trend agents to read
    /// rendered K-line PNGs.</summary>
    public string VisionModel { get; set; } = "gpt-4o";

    /// <summary>How many bars to ship to the analyst on each run. Defaults to a window
    /// large enough for the classical pattern catalog (~50 bars).</summary>
    public int BarCount { get; set; } = 50;

    /// <summary>Wall-clock ceiling for one /analyst/run call. The Python side has its
    /// own 60s top-level ceiling — keep this at or below that.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>When true, <c>AiAnalystEnricher</c> appends an AI verdict line to every
    /// Signal/Trade notification before the dispatcher fans it out.</summary>
    public bool IncludeInEnricher { get; set; }

    /// <summary>Base64-encoded DPAPI ciphertext for the LLM provider's API key. Bound
    /// directly from <c>notifications.json</c>; never edited by hand.</summary>
    public string? ApiKeyEncryptedBase64 { get; set; }

    /// <summary>Plain-text API key. Reads decrypt the ciphertext; writes encrypt before
    /// storing. Marked <see cref="JsonIgnoreAttribute"/> so it never round-trips through
    /// the on-disk JSON.</summary>
    [JsonIgnore]
    public string? ApiKey
    {
        get => DecryptDpapi(ApiKeyEncryptedBase64);
        set => ApiKeyEncryptedBase64 = EncryptDpapi(value);
    }

    private static string? DecryptDpapi(string? encryptedBase64)
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

    private static string? EncryptDpapi(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }
}
