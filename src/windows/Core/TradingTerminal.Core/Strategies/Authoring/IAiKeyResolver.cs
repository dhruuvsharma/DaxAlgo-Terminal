namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Resolves a codegen provider's API key. Keys live in the shell's DPAPI credential store, which the
/// lower layers can't reference, so the shell implements this and registers it; the codegen factory
/// resolves keys through it. The <see cref="Null"/> resolver (no keys) is the fallback — then only
/// keyless providers work (installed agent CLIs, local Ollama), which is a valid, useful state.
/// </summary>
public interface IAiKeyResolver
{
    /// <summary>The API key for <paramref name="providerId"/> (e.g. <c>openai</c>, <c>deepseek</c>,
    /// <c>anthropic</c>), or null when none is configured.</summary>
    string? Resolve(string providerId);

    /// <summary>A resolver that never has a key — only keyless providers (agent CLIs, Ollama) are usable.</summary>
    public static IAiKeyResolver Null { get; } = new NullAiKeyResolver();
}

internal sealed class NullAiKeyResolver : IAiKeyResolver
{
    public string? Resolve(string providerId) => null;
}
