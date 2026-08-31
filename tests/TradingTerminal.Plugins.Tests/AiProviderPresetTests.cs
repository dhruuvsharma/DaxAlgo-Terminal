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
