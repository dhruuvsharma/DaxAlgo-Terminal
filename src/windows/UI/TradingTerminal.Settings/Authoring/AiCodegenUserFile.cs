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
    /// <summary>
    /// Absolute path to <c>%LocalAppData%\DaxAlgo Terminal\ai-codegen.json</c>. The directory is created
    /// on first write.
    ///
    /// <para>Settable so a test can redirect it. Without that, a test for any of the writers below edits
    /// the configuration of whoever is running it — which is exactly what happened: a suite rewrote a
    /// developer's own provider settings and reported green. Setting null restores the default.</para>
    ///
    /// <para><b>Computed rather than initialised, and that is not a style preference.</b> This was first
    /// written as <c>public static string Path { get; set; } = DefaultPath;</c> declared <i>above</i>
    /// <c>DefaultPath</c>. Static initialisers run in textual order, so <c>DefaultPath</c> was still null
    /// when <c>Path</c> captured it, and the application died at startup on <c>AddJsonFile(null)</c> —
    /// "File path must be a non-empty string". No test caught it: every test assigns <c>Path</c> before
    /// reading it, so none of them ever observes the default. Swapping the two declarations would also
    /// fix it, and would leave the same trap armed for whoever tidies this file next — a computed getter
    /// cannot be broken by reordering.</para>
    /// </summary>
    public static string Path
    {
        get => _redirect ?? DefaultPath;
        set => _redirect = value;
    }

    private static string? _redirect;

    /// <summary>The real per-user location, kept separately so a test can put <see cref="Path"/> back.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "ai-codegen.json");

    /// <summary>
    /// Records the model + reasoning effort the user picked for a provider, and makes that provider the
    /// default. Merges into whatever the file already holds, so switching provider doesn't forget the
    /// choices made for the previous one (and never touches keys in other sections).
    /// <paramref name="buildEffort"/> is the pipeline-wide build effort (<c>quick</c>/<c>standard</c>/
    /// <c>deep</c>/<c>max</c>); null leaves whatever the file already records.
    /// </summary>
    public static void SaveSelection(
        string providerId, string? model, CodegenEffort effort, AiCodegenOptions current, string? buildEffort = null)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;

        var root = Read();
        var section = Section(root);

        section["DefaultProvider"] = providerId;

        // Session-wide, not per-provider: the build pipeline's effort is about how hard the BUILDER
        // works (skills, fix attempts, review, smoke), which doesn't change with the provider.
        if (buildEffort is not null) section["BuildEffort"] = buildEffort;

        var provider = Provider(section, providerId);

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

        Write(root);
    }

    /// <summary>
    /// Records one provider's endpoint, model and CLI profile — what the provider settings pane saves.
    ///
    /// <para>Separate from <see cref="SaveSelection"/> because editing a provider's setup is not choosing
    /// it: a user can add a key to a second provider without wanting the next build to go there.</para>
    /// </summary>
    public static void SaveProvider(string providerId, AiCodegenProvider config)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;
        ArgumentNullException.ThrowIfNull(config);

        var root = Read();
        var provider = Provider(Section(root), providerId);

        provider["Kind"] = config.Kind.ToString();
        provider["BaseUrl"] = Blank(config.BaseUrl);
        provider["Model"] = Blank(config.Model);
        provider["CliProfile"] = Blank(config.CliProfile);

        Write(root);
    }

    /// <summary>Records which provider the builder should open with, touching nothing else.</summary>
    public static void SaveDefaultProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return;

        var root = Read();
        Section(root)["DefaultProvider"] = providerId;
        Write(root);
    }

    /// <summary>Null rather than an empty string, so a blank field falls through to whatever
    /// <c>appsettings.json</c> ships instead of overriding it with nothing.</summary>
    private static JsonNode? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value);

    /// <summary>The file as it stands. A corrupt or empty file starts over rather than throwing — losing a
    /// remembered model choice is a smaller harm than a builder that will not open.</summary>
    private static JsonObject Read()
    {
        if (!File.Exists(Path)) return new JsonObject();

        try
        {
            var existing = File.ReadAllText(Path);
            return string.IsNullOrWhiteSpace(existing)
                ? new JsonObject()
                : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonObject Section(JsonObject root)
    {
        if (root[AiCodegenOptions.SectionName] is JsonObject section) return section;

        section = new JsonObject();
        root[AiCodegenOptions.SectionName] = section;
        return section;
    }

    private static JsonObject Provider(JsonObject section, string providerId)
    {
        if (section["Providers"] is not JsonObject providers)
        {
            providers = new JsonObject();
            section["Providers"] = providers;
        }

        if (providers[providerId] is JsonObject provider) return provider;

        provider = new JsonObject();
        providers[providerId] = provider;
        return provider;
    }

    private static void Write(JsonObject root)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
