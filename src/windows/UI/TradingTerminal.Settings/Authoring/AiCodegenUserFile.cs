using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Persists the AI builder's provider + model choice to a per-user JSON file, layered last into the host
/// configuration (like <c>notifications.json</c>), so the picker comes back where the user left it and
/// <c>appsettings.json</c> stays the shipped default. API keys are NOT here — they live in the DPAPI
/// credential store; this file holds only the non-secret provider/model/endpoint config.
/// </summary>
public static class AiCodegenUserFile
{
    /// <summary>Absolute path to <c>%LocalAppData%\DaxAlgo Terminal\ai-codegen.json</c>.
    /// The directory is created on first write.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "ai-codegen.json");

    /// <summary>
    /// Records the model + reasoning effort the user picked for a provider, and makes that provider the
    /// default. Merges into whatever the file already holds, so switching provider doesn't forget the
    /// choices made for the previous one (and never touches keys in other sections).
    /// </summary>
    public static void SaveSelection(string providerId, string? model, CodegenEffort effort, AiCodegenOptions current)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(Path))
        {
            var existing = File.ReadAllText(Path);
            root = string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root[AiCodegenOptions.SectionName] is not JsonObject section)
        {
            section = new JsonObject();
            root[AiCodegenOptions.SectionName] = section;
        }

        section["DefaultProvider"] = providerId;

        if (section["Providers"] is not JsonObject providers)
        {
            providers = new JsonObject();
            section["Providers"] = providers;
        }

        if (providers[providerId] is not JsonObject provider)
        {
            provider = new JsonObject();
            providers[providerId] = provider;
        }

        // Keep the endpoint/kind the app is configured with — this file only overrides the model, so a
        // later appsettings change to a base URL still reaches the user.
        if (current.Providers.TryGetValue(providerId, out var configured))
        {
            if (!string.IsNullOrWhiteSpace(configured.BaseUrl)) provider["BaseUrl"] = configured.BaseUrl;
            provider["Kind"] = configured.Kind.ToString();
        }

        provider["Model"] = string.IsNullOrWhiteSpace(model) ? null : model;

        // Empty ⇒ "provider default", which means the effort parameter is never sent — the only setting
        // a model that predates it will accept.
        provider["Effort"] = effort.Wire();

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
