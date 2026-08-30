using System.Net;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Which providers the builder can actually reach.
///
/// <para>"Bring your own provider" was true of the configuration file and false of the product: the
/// settings pane could edit the providers <c>appsettings.json</c> shipped and could not add one, and
/// the wire kinds were OpenAI-shaped or Anthropic-shaped with nothing in between. These pin the three
/// things that changed — a third wire shape, a keyless local endpoint under any name, and a provider
/// the user named themselves.</para>
/// </summary>
public sealed class CodegenProviderReachTests
{
    private static StrategyCodegenClientFactory Factory(
        string id, AiCodegenProvider provider, string? key = "sk-test") =>
        new(() => new HttpClient(new NeverSendsHandler()),
            new AiCodegenOptions { Providers = { [id] = provider } },
            _ => key);

    // ── Azure ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AzureIsAWireKindOfItsOwn()
    {
        // Its bodies are the OpenAI ones; the envelope is not. Before this there was no way to say so
        // in configuration at all.
        Enum.GetValues<AiCodegenProviderKind>().Should().Contain(AiCodegenProviderKind.AzureOpenAi);
    }

    [Fact]
    public async Task AzurePutsTheDeploymentInThePathAndTheKeyInItsOwnHeader()
    {
        // Three differences, each of which alone is a failure the user cannot read: the deployment in
        // the body is a 404, a missing api-version is a 404, and a bearer token is a 401 with a
        // perfectly good key.
        var captured = new CapturingHandler();
        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(captured), "azure", "Azure OpenAI",
            "https://contoso.openai.azure.com", "my-deployment", "azure-key",
            azureApiVersion: "2024-10-21");

        await client.GenerateAsync(new StrategyCodegenRequest("system", [new(CodegenRole.User, "hi")]));

        var request = captured.Request!;
        request.RequestUri!.ToString().Should()
            .Be("https://contoso.openai.azure.com/openai/deployments/my-deployment/chat/completions?api-version=2024-10-21");
        request.Headers.GetValues("api-key").Should().ContainSingle().Which.Should().Be("azure-key");
        request.Headers.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task AnOrdinaryProviderStillGetsABearerTokenAndThePlainPath()
    {
        var captured = new CapturingHandler();
        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(captured), "groq", "Groq", "https://api.groq.com/openai/v1", "some-model", "gsk-test");

        await client.GenerateAsync(new StrategyCodegenRequest("system", [new(CodegenRole.User, "hi")]));

        var request = captured.Request!;
        request.RequestUri!.ToString().Should().Be("https://api.groq.com/openai/v1/chat/completions");
        request.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Bearer gsk-test");
    }

    [Fact]
    public void AnAzureProviderWithNoApiVersionFallsBackRatherThanAskingForNone()
    {
        // An Azure request with no api-version is a 404, so "unset" cannot be passed through.
        var factory = Factory("azure", new AiCodegenProvider
        {
            BaseUrl = "https://contoso.openai.azure.com",
            Model = "my-deployment",
            Kind = AiCodegenProviderKind.AzureOpenAi,
        });

        factory.Build("azure", model: null)!.IsAvailable.Should().BeTrue();
        AiCodegenProvider.DefaultAzureApiVersion.Should().NotBeNullOrWhiteSpace();
    }

    // ── Local servers ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AProviderOnThisMachineNeedsNoKeyWhateverItIsCalled()
    {
        // Only the provider literally named "ollama" was exempt, so a user pointing a provider at
        // localhost:1234 got a row saying "no key yet" about a server that does not want one.
        var factory = Factory("lmstudio", new AiCodegenProvider
        {
            BaseUrl = "http://localhost:1234/v1",
            Model = "some-local-model",
        }, key: null);

        factory.Build("lmstudio", model: null)!.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void AHostedProviderWithNoKeyIsStillUnavailable()
    {
        // The loopback exemption must not become a general one.
        var factory = Factory("groq", new AiCodegenProvider
        {
            BaseUrl = "https://api.groq.com/openai/v1",
            Model = "some-model",
        }, key: null);

        factory.Build("groq", model: null)!.IsAvailable.Should().BeFalse();
    }

    // ── Names ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AProviderTheUserNamedShowsThatNameRatherThanItsId()
    {
        // The id is a slug the user never chose to look at. Showing it is how a picker ends up reading
        // "my-company-s-gateway (API key)".
        var factory = Factory("my-company-s-gateway", new AiCodegenProvider
        {
            BaseUrl = "https://gateway.example.com/v1",
            Model = "internal",
            DisplayName = "Company gateway",
        });

        factory.Build("my-company-s-gateway", model: null)!.DisplayName.Should().Be("Company gateway");
    }

    // ── The catalog ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPresetCarriesAnEndpointAndNoModelId()
    {
        // Deliberate: a base URL is stable for years and a model id for weeks. appsettings.json already
        // records a free model withdrawn one week after it was named. Every endpoint here serves
        // GET /models, so the picker's refresh knows what a key reaches today.
        foreach (var preset in AiProviderCatalog.Presets)
        {
            preset.Id.Should().NotBeNullOrWhiteSpace();
            preset.DisplayName.Should().NotBeNullOrWhiteSpace();
            preset.Note.Should().NotBeNullOrWhiteSpace();

            if (preset.IsBlank) continue;

            Uri.TryCreate(preset.BaseUrl, UriKind.Absolute, out var uri).Should()
                .BeTrue($"'{preset.DisplayName}' should carry a usable endpoint");
            uri!.Scheme.Should().BeOneOf(Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        }
    }

    [Fact]
    public void ThereIsExactlyOneBlankRow_BecauseItIsTheAnswerToAnyProviderNotListed()
    {
        AiProviderCatalog.Presets.Should().ContainSingle(p => p.IsBlank);
        AiProviderCatalog.Presets[^1].IsBlank.Should().BeTrue("the blank row belongs last");
    }

    [Fact]
    public void LocalPresetsAreRecognisedAsLocal()
    {
        AiProviderCatalog.Find("lmstudio")!.IsLocal.Should().BeTrue();
        AiProviderCatalog.Find("groq")!.IsLocal.Should().BeFalse();
    }

    [Theory]
    [InlineData("My Company's Gateway", "my-company-s-gateway")]
    [InlineData("  Groq  ", "groq")]
    [InlineData("GPT-4 Proxy", "gpt-4-proxy")]
    [InlineData("!!!", "custom")]
    [InlineData("", "custom")]
    public void AnIdIsASlugOfWhateverTheUserTyped(string name, string expected)
    {
        // The id is a configuration key AND a credential-store key, so it has to be predictable and
        // need no escaping.
        AiProviderCatalog.IdFrom(name).Should().Be(expected);
    }

    [Fact]
    public void ASecondProviderNeverTakesAnIdThatIsAlreadyTaken()
    {
        // Reusing an id would overwrite the first provider's config and its stored key — a collision
        // discovered later as a key that stopped working.
        AiProviderCatalog.UniqueId("groq", ["groq"]).Should().Be("groq-2");
        AiProviderCatalog.UniqueId("groq", ["groq", "groq-2"]).Should().Be("groq-3");
        AiProviderCatalog.UniqueId("groq", ["openai"]).Should().Be("groq");
    }

    // ── handlers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records the request and answers with an empty completion — enough to see the envelope
    /// without a network.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}"""),
            });
        }
    }

    private sealed class NeverSendsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("these tests only build clients");
    }
}
