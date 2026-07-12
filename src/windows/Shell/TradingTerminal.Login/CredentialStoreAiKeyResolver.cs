using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Login;

/// <summary>
/// Resolves AI-provider keys for the codegen factory from the DPAPI <see cref="AiKeyStore"/>, falling
/// back to a <c>{PROVIDER}_API_KEY</c> environment variable (so a key set for the CLI works in the app
/// too). Registered by each shell in place of the <see cref="IAiKeyResolver.Null"/> default, which
/// unlocks the keyed codegen providers.
/// </summary>
public sealed class CredentialStoreAiKeyResolver(AiKeyStore store) : IAiKeyResolver
{
    private readonly AiKeyStore _store = store;

    public string? Resolve(string providerId)
    {
        var stored = _store.Get(providerId);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        var fromEnv = Environment.GetEnvironmentVariable($"{providerId.ToUpperInvariant()}_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}
