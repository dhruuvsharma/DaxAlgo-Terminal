using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Which Claude models Hyperion offers, and what signing in instead of pasting a key does to them.
///
/// <para>Three defects, one theme — the model list drifting away from what is actually true: the
/// shipped configuration pointed the keyed provider at a retired model; the signed-in provider was
/// an unknown id to the catalog, so it lost the shortlist, Research and vision; and an installed but
/// signed-out CLI was offered as Ready.</para>
/// </summary>
public sealed class AnthropicModelsAndSignInTests
{
    [Fact]
    public void The_shortlist_leads_with_the_current_flagship()
    {
        var models = AiModelCatalog.For("anthropic");

        models[0].Should().Be("claude-opus-5-5", "the first entry is what an unconfigured provider opens on");
        AiModelCatalog.AnthropicDefault.Should().Be(models[0]);
        models.Should().Contain(["claude-fable-5-1", "claude-sonnet-5", "claude-haiku-4-5"]);
        models.Should().NotContain(m => m.StartsWith("claude-3", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shipped keyed provider names a model somebody can still call.
    ///
    /// <para>It named <c>claude-3-5-sonnet-latest</c>, retired — so the first thing a user who pasted
    /// an Anthropic key got was a failed request that read as a bad key. Read from the real file, because
    /// a hand-copied fixture is exactly how it went unnoticed.</para>
    /// </summary>
    [Fact]
    public void The_shipped_anthropic_provider_names_a_model_on_the_shortlist()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "appsettings.json")));

        var model = settings.RootElement
            .GetProperty("AiCodegen").GetProperty("Providers").GetProperty("anthropic")
            .GetProperty("Model").GetString();

        AiModelCatalog.For("anthropic").Should().Contain(model);
    }

    [Fact]
    public void Signing_in_reaches_the_same_models_as_a_key()
    {
        const string signedIn = StrategyCodegenClientFactory.AnthropicOAuthId;

        AiModelCatalog.For(signedIn).Should().Equal(AiModelCatalog.For("anthropic"),
            "the credential changes who is billed, not what the models are");
        AiModelCatalog.ResearchEffort(signedIn, "claude-opus-5-5").Should().Be(CodegenEffort.Max);
        AiModelCatalog.SupportsEffort(signedIn, "claude-opus-5-5").Should().BeTrue();
        AiModelCatalog.SupportsVision(signedIn, "claude-opus-5-5").Should().BeTrue();
    }

    /// <summary>Haiku 4.5 answers an effort parameter with a 400, so none may be sent — not by Research,
    /// not by the critics' step-down retry, and not through a gateway either.</summary>
    [Fact]
    public void Haiku_is_never_sent_an_effort()
    {
        AiModelCatalog.SupportsEffort("anthropic", "claude-haiku-4-5").Should().BeFalse();
        AiModelCatalog.SupportsEffort("openrouter", "anthropic/claude-haiku-4.5").Should().BeFalse();
        AiModelCatalog.ResearchEffort("anthropic", "claude-haiku-4-5").Should().Be(CodegenEffort.Default);
        AiModelCatalog.ResearchUnavailable("anthropic", "claude-haiku-4-5").Should().NotBeNull(
            "Research falling back has to say so");
    }

    /// <summary>
    /// One Anthropic row in the settings pane, one model: the row saves it under <c>anthropic</c>, and
    /// the signed-in client used to read only <c>anthropic-oauth</c> — so a model picked on the sign-in
    /// half was ignored.
    /// </summary>
    [Fact]
    public void The_signed_in_client_uses_the_anthropic_rows_model()
    {
        var options = new AiCodegenOptions
        {
            Providers =
            {
                ["anthropic"] = new AiCodegenProvider
                {
                    Kind = AiCodegenProviderKind.Anthropic, Model = "claude-sonnet-5",
                },
            },
        };

        Factory(options).Build(StrategyCodegenClientFactory.AnthropicOAuthId, model: null)!
            .Model.Should().Be("claude-sonnet-5");

        Factory(new AiCodegenOptions()).Build(StrategyCodegenClientFactory.AnthropicOAuthId, model: null)!
            .Model.Should().Be(AiModelCatalog.AnthropicDefault);
    }

    [Fact]
    public void The_account_is_read_from_the_status_line()
    {
        AnthropicOAuthCli.DescribeAccount("Credentials\n  Logged in to Acme Trading as dev@acme.io\n")
            .Should().Be("dev@acme.io (Acme Trading)");

        // The real signed-out output (captured 2026-09-03) names no account.
        AnthropicOAuthCli.DescribeAccount(
                "Credentials\n  (profile \"default\" not configured — run `ant auth login` to set it up)\n")
            .Should().BeNull();

        AnthropicOAuthCli.DescribeAccount(null).Should().BeNull();
    }

    /// <summary>
    /// Installed is not signed in. Before a check the sign-in is given the benefit of the doubt; once a
    /// check has said no, the provider is not offered as working.
    /// </summary>
    [Fact]
    public async Task A_check_that_finds_nobody_takes_the_sign_in_off_the_ready_list()
    {
        // A path that exists as far as the resolver is concerned and cannot be launched: the check
        // fails the way a broken or signed-out CLI does, without touching the real one.
        var missing = Path.Combine(Path.GetTempPath(), "daxalgo-no-ant-" + Guid.NewGuid().ToString("N"), "ant.exe");
        var oauth = new AnthropicOAuthCli(
            resolveOnPath: name => name == "ant" ? missing : null,
            searchDirectories: () => []);

        var factory = Factory(new AiCodegenOptions(), oauth);
        SignInClient(factory).IsAvailable.Should().BeTrue("nobody has asked yet");

        (await oauth.IsSignedInAsync()).Should().BeFalse();

        oauth.SignedIn.Should().BeFalse();
        oauth.Account.Should().BeNull();
        oauth.IsUsable.Should().BeFalse();
        SignInClient(factory).IsAvailable.Should().BeFalse("a check said nobody is signed in");
    }

    private static IStrategyCodegenClient SignInClient(StrategyCodegenClientFactory factory) =>
        factory.BuildAll().Single(c => c.ProviderId == StrategyCodegenClientFactory.AnthropicOAuthId);

    private static StrategyCodegenClientFactory Factory(AiCodegenOptions options, AnthropicOAuthCli? oauth = null) =>
        new(() => new HttpClient(), options, _ => null,
            oauth ?? new AnthropicOAuthCli(resolveOnPath: _ => null, searchDirectories: () => []));

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
