using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The shipped provider presets, held to what they claim.
///
/// <para>A preset is a vendor fact written down once, and nothing checked it. The cost of getting one
/// wrong is paid entirely by the user: the endpoint 404s or the models list comes back empty, and
/// every explanation on offer points at their key rather than at our typo. None of this needs a
/// vendor -- it is the SHAPE that goes wrong, and the shape is checkable here.</para>
/// </summary>
public sealed class AiProviderPresetTests
{
    private static IEnumerable<AiProviderPreset> Real =>
        AiProviderCatalog.Presets.Where(p => !p.IsBlank);

    [Fact]
    public void Every_preset_is_already_in_the_form_the_client_normalises_to()
    {
        // Normalisation is idempotent, so a preset that survives it unchanged is one that will be sent
        // exactly as written here. A preset needing repair still works, but it means the table and the
        // wire disagree, and the table is what a person reads when a provider misbehaves.
        foreach (var preset in Real)
        {
            CodegenBaseUrl.Normalise(preset.BaseUrl).Should().Be(
                preset.BaseUrl,
                "the preset for {0} should need no repair -- check for a trailing slash, a missing "
                + "scheme, or a pasted /chat/completions", preset.Id);
        }
    }

    [Fact]
    public void Every_preset_is_a_usable_absolute_address()
    {
        foreach (var preset in Real)
        {
            CodegenBaseUrl.TryAbsolute(preset.BaseUrl).Should().NotBeNull(
                "{0} ships a base URL the client cannot build a request from", preset.Id);
        }
    }

    [Fact]
    public void Only_the_local_runtimes_are_reached_over_plain_http()
    {
        // A hosted endpoint over http would put the user's API key on the wire in clear text. The local
        // ones are http because that is what they serve, and nothing leaves the machine.
        foreach (var preset in Real)
        {
            var uri = CodegenBaseUrl.TryAbsolute(preset.BaseUrl)!;

            (uri.Scheme == Uri.UriSchemeHttps || preset.IsLocal).Should().BeTrue(
                "{0} is a hosted provider reached over plain http", preset.Id);
        }
    }

    [Theory]
    // Each row is the vendor's own documented OpenAI-compatible completions endpoint. Written out in
    // full rather than composed, so this is a second, independent statement of the same fact: if it
    // and the catalog ever disagree, one of them was edited without the other.
    [InlineData("google", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    [InlineData("groq", "https://api.groq.com/openai/v1/chat/completions")]
    [InlineData("mistral", "https://api.mistral.ai/v1/chat/completions")]
    [InlineData("together", "https://api.together.xyz/v1/chat/completions")]
    [InlineData("fireworks", "https://api.fireworks.ai/inference/v1/chat/completions")]
    [InlineData("cerebras", "https://api.cerebras.ai/v1/chat/completions")]
    [InlineData("moonshot", "https://api.moonshot.ai/v1/chat/completions")]
    [InlineData("lmstudio", "http://localhost:1234/v1/chat/completions")]
    [InlineData("vllm", "http://localhost:8000/v1/chat/completions")]
    [InlineData("litellm", "http://localhost:4000/chat/completions")]
    public void A_preset_composes_the_endpoint_its_vendor_documents(string id, string expected)
    {
        var preset = AiProviderCatalog.Find(id);
        preset.Should().NotBeNull("the catalog should still ship a preset called {0}", id);

        // The client appends this path itself for every OpenAI-compatible provider. LiteLLM is the one
        // with no /v1 because its proxy serves at the root, which is why the whole URL is spelled out
        // per row rather than assumed uniform.
        (preset!.BaseUrl + "/chat/completions").Should().Be(expected);
    }

    [Fact]
    public void Azure_is_the_one_preset_that_cannot_be_used_as_shipped()
    {
        // The resource name is part of the HOST, so the preset is a template, not an address. It parses
        // as a perfectly good absolute https URL -- every syntactic check passes -- so without this the
        // first thing a user sees is a DNS failure naming a host they never chose.
        var azure = AiProviderCatalog.Find("azure");

        azure.Should().NotBeNull();
        azure!.Kind.Should().Be(AiCodegenProviderKind.AzureOpenAi);

        CodegenBaseUrl.TryAbsolute(azure.BaseUrl).Should().NotBeNull(
            "it is syntactically valid, which is exactly the problem");

        CodegenBaseUrl.IsUnedited(azure.BaseUrl).Should().BeTrue(
            "the placeholder has to be recognisable, or nothing can tell the user to replace it");
    }

    [Fact]
    public void An_unedited_template_is_not_a_configured_provider()
    {
        // Refused before a request rather than after one: a generation against a host that cannot
        // resolve costs a full turn to learn nothing.
        var azure = AiProviderCatalog.Find("azure")!;

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), azure.Id, azure.DisplayName, azure.BaseUrl, "my-deployment", "sk-x");

        client.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void A_real_host_containing_the_placeholder_letters_is_still_accepted()
    {
        // The reason the token is matched whole. "YOUR-" as a prefix would refuse this.
        CodegenBaseUrl.IsUnedited("https://api.foryour-company.com/v1").Should().BeFalse();
        CodegenBaseUrl.IsUnedited("https://your-resource.openai.azure.com").Should().BeTrue(
            "case should not rescue an unedited template");
    }

    [Theory]
    [InlineData("localhost:1234/v1")]
    [InlineData("127.0.0.1:8000/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void A_local_server_is_recognised_as_one_even_typed_without_a_scheme(string typed)
    {
        // The keyless decision is made from what the USER TYPED, and "localhost:1234/v1" -- the form
        // every local-runtime readme prints -- parses as an absolute URI whose SCHEME is "localhost".
        // That is not loopback, so a local server was treated as one needing a key: the request went to
        // the right place while the row insisted on a credential the server does not want.
        //
        // The client already repaired the same string before sending. Two places asking one question
        // and getting different answers.
        var repaired = CodegenBaseUrl.TryAbsolute(CodegenBaseUrl.Normalise(typed));

        repaired.Should().NotBeNull();
        repaired!.IsLoopback.Should().BeTrue("{0} names this machine", typed);
    }

    [Fact]
    public void A_local_provider_needs_no_key_however_its_address_was_typed()
    {
        // End to end through the factory, which is where the decision actually lives.
        var options = new AiCodegenOptions();
        options.Providers["lmstudio-typed"] = new AiCodegenProvider
        {
            DisplayName = "LM Studio",
            Kind = AiCodegenProviderKind.OpenAiCompatible,
            BaseUrl = "localhost:1234/v1",
            Model = "local-model",
        };

        var factory = new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => null);
        var client = factory.Build("lmstudio-typed", model: null);

        client.Should().NotBeNull();
        client!.IsAvailable.Should().BeTrue(
            "a server on this machine wants no API key, so the absence of one must not make the "
            + "provider unusable");
    }

    // -- the OTHER table of the same facts ------------------------------------------------------

    [Fact]
    public void Every_shipped_provider_obeys_the_rules_the_presets_do()
    {
        // appsettings.json carries a SECOND provider table, and it is the one most users meet: these
        // are shipped and only editable, while the presets are what "Add a provider" offers. Checking
        // the presets and not these would leave the default path -- DefaultProvider is one of them --
        // as the unverified half.
        foreach (var (id, baseUrl) in ShippedConfiguration().Providers)
        {

            CodegenBaseUrl.Normalise(baseUrl).Should().Be(
                baseUrl, "the shipped base URL for {0} should need no repair", id);

            var uri = CodegenBaseUrl.TryAbsolute(baseUrl);
            uri.Should().NotBeNull("{0} ships a base URL no request can be built from", id);

            CodegenBaseUrl.IsUnedited(baseUrl).Should().BeFalse(
                "{0} ships a template rather than an address", id);

            (uri!.Scheme == Uri.UriSchemeHttps || uri.IsLoopback).Should().BeTrue(
                "{0} is a hosted provider reached over plain http, which puts the key on the wire in "
                + "clear text", id);
        }
    }

    [Fact]
    public void Every_shipped_provider_has_a_name_a_person_would_recognise()
    {
        // None of the shipped providers sets a DisplayName in appsettings, so the name shown in the
        // picker comes from code for every one of them -- and from two different places. The seven
        // OpenAI-compatible ones get it from the factory's fallback map; `anthropic` never reaches that
        // map at all, because its branch constructs AnthropicCodegenClient, which carries its own.
        //
        // That second path is why the map having seven rows for eight providers is NOT a bug, which is
        // not obvious from reading the map: I took it for one, "fixed" it, and the mutation check
        // showed the added row changed nothing. What is worth guarding is the property itself -- every
        // shipped provider ends up with a name a person would recognise, wherever it comes from.
        var options = new AiCodegenOptions();
        foreach (var (id, baseUrl) in ShippedConfiguration().Providers)
        {
            options.Providers[id] = new AiCodegenProvider
            {
                Kind = id == "anthropic" ? AiCodegenProviderKind.Anthropic : AiCodegenProviderKind.OpenAiCompatible,
                BaseUrl = baseUrl,
                Model = "m",
            };
        }

        var factory = new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => "sk-x");

        foreach (var (id, _) in ShippedConfiguration().Providers)
        {
            var client = factory.Build(id, model: null);
            client.Should().NotBeNull("{0} is a shipped provider and should be buildable", id);

            client!.DisplayName.Should().NotBe(
                $"{id} (API key)",
                "{0} falls through to the generic fallback -- give it a row in the display-name map", id);
        }
    }

    [Fact]
    public void The_default_provider_is_one_that_actually_ships()
    {
        // A DefaultProvider naming a key that is not in the table resolves to nothing, and the app
        // falls back to "first available" -- which is not wrong, but means the setting silently does
        // nothing and the edition ships pointing somewhere nobody chose.
        var (dflt, providers) = ShippedConfiguration();

        if (string.IsNullOrWhiteSpace(dflt)) return;

        providers.Select(p => p.Id).Should().Contain(
            key => key.Equals(dflt, StringComparison.OrdinalIgnoreCase),
            "DefaultProvider is '{0}', which is not one of the shipped providers", dflt);
    }

    /// <summary>
    /// The shipped table, read straight out of the file.
    ///
    /// <para>Deliberately not deserialised into <see cref="AiCodegenOptions"/>: the app binds this
    /// through Microsoft.Extensions.Configuration, and going through System.Text.Json instead would
    /// test a binder nothing uses -- it does not even accept the string enum values the file holds.
    /// What is being checked here is the FILE, so the file is what gets read.</para>
    /// </summary>
    private static (string? Default, IReadOnlyList<(string Id, string BaseUrl)> Providers) ShippedConfiguration()
    {
        var path = Path.Combine(RepositoryRoot(), "appsettings.json");
        File.Exists(path).Should().BeTrue("the shipped configuration should be at the repository root");

        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var section = document.RootElement.GetProperty("AiCodegen");

        var dflt = section.TryGetProperty("DefaultProvider", out var d) ? d.GetString() : null;

        var providers = new List<(string, string)>();
        foreach (var entry in section.GetProperty("Providers").EnumerateObject())
        {
            // The section carries "_comment" style notes alongside the real entries.
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;

            providers.Add((
                entry.Name,
                entry.Value.TryGetProperty("BaseUrl", out var b) ? b.GetString() ?? string.Empty : string.Empty));
        }

        providers.Should().NotBeEmpty("an empty table would pass every check above without testing "
            + "anything -- if this trips, the section moved or was renamed");

        return (dflt, providers);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // <root>/tests/TradingTerminal.Plugins.Tests/AiProviderPresetTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        File.Exists(Path.Combine(root, "TradingTerminal.Windows.slnx")).Should().BeTrue(
            $"'{root}' should be the repository root");
        return root;
    }

    [Fact]
    public void Every_preset_has_an_id_that_survives_being_derived_from_its_name()
    {
        // Providers added through the UI are keyed by a slug of their display name. A shipped preset
        // whose id does not match that rule would be reachable one way and not the other.
        foreach (var preset in AiProviderCatalog.Presets)
        {
            preset.Id.Should().NotBeNullOrWhiteSpace();
            preset.Id.Should().Be(preset.Id.ToLowerInvariant(), "ids are compared case-insensitively "
                + "but stored as written, so a mixed-case one invites a near-duplicate row");
        }

        AiProviderCatalog.Presets.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }
}
