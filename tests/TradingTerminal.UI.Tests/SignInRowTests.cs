using System.Net.Http;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The sign-in row, driven through the REAL provider factory rather than a fake.
///
/// <para><b>Because the fake could not have caught this.</b> The pane is fed by
/// <c>StrategyCodegenClientFactory.BuildAll()</c>, and every test above hands the view-model a
/// hand-written list of clients instead — so "which clients actually reach the pane, under the
/// configuration the application ships" was asserted nowhere. That is the same shape as every other
/// defect in this area: built, unit-tested, never reached on the path that runs.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class SignInRowTests(ITestOutputHelper output)
{
    /// <summary>The providers appsettings.json actually ships, trimmed to what matters here.</summary>
    private static AiCodegenOptions Shipped() => new()
    {
        Providers =
        {
            ["openai"] = new AiCodegenProvider
            {
                BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini",
            },
            ["anthropic"] = new AiCodegenProvider
            {
                Kind = AiCodegenProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com", Model = "claude-opus-5",
            },
        },
    };

    private static AiProviderSettingsViewModel Pane(AiCodegenOptions options, bool antInstalled)
    {
        // ONE wrapper, shared by the factory and the pane -- which is the point of injecting it. Two
        // independently constructed ones can disagree: the pane said the CLI was missing while the
        // provider list said it was there.
        var oauth = new AnthropicOAuthCli(
            resolveOnPath: name => antInstalled && name == "ant" ? @"C:	oolsnt.exe" : null,

            // Empty, or the well-known-installer search finds the REAL ant.exe on whoever's machine is
            // running the suite and "not installed" stops being a state a test can express.
            searchDirectories: () => []);

        var factory = new StrategyCodegenClientFactory(
            () => new HttpClient(), options, _ => null, oauth: oauth);

        var builder = new AiStrategyBuilder(
            factory,
            new StrategyCodegenOrchestrator(new RoslynStrategyCompiler(), logger: null),
            StrategyContextPack.Load(),
            options);

        return new AiProviderSettingsViewModel(
            builder, Microsoft.Extensions.Options.Options.Create(options), keys: null, logger: null,
            oauth: oauth);
    }

    /// <summary>
    /// THE REPORT: "the only thing I can see is API key, in both the API key option and the sign-in
    /// option." With no key stored, the Anthropic row must open on SIGN IN and show the sign-in
    /// controls — not a key box.
    /// </summary>
    [Fact]
    public void The_anthropic_row_opens_on_sign_in_when_there_is_no_key()
    {
        var pane = Pane(Shipped(), antInstalled: true);

        foreach (var row in pane.Providers)
            output.WriteLine($"{row.ProviderId,-18} supportsSignIn={row.SupportsSignIn} useSignIn={row.UseSignIn} isSignIn={row.IsSignIn} takesKey={row.TakesKey} signal={row.Signal}");

        var anthropic = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        Assert.True(anthropic.UseSignIn, "no key is stored, so the recommended path is signing in");
        Assert.True(anthropic.IsSignIn, "the sign-in controls must be the ones on screen");
        Assert.False(anthropic.TakesKey, "a key box here is the whole complaint");
    }

    /// <summary>Exactly one Anthropic row reaches the pane through the real factory.</summary>
    [Fact]
    public void Only_one_anthropic_row_reaches_the_pane()
    {
        var pane = Pane(Shipped(), antInstalled: true);

        Assert.Single(pane.Providers, r =>
            r.ProviderId.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Toggling the segment swaps which half is on screen.</summary>
    [Fact]
    public void The_segment_swaps_the_two_halves()
    {
        var pane = Pane(Shipped(), antInstalled: true);
        var row = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        row.UseApiKey = true;
        Assert.True(row.TakesKey);
        Assert.False(row.IsSignIn);

        row.UseApiKey = false;
        Assert.True(row.IsSignIn);
        Assert.False(row.TakesKey);
    }

    /// <summary>
    /// THE REPORT: "I'm unable to click on sign in with Anthropic, it's blocked."
    ///
    /// <para>With no key AND no CLI, the row must NOT open on the sign-in half. Signing in needs
    /// Anthropic's own CLI, so on a machine without it that half is a disabled button and nothing else
    /// — the one row the user has to configure, opened onto the one thing they cannot do.</para>
    /// </summary>
    [Fact]
    public void With_no_key_and_no_cli_the_row_opens_on_the_key_half()
    {
        var pane = Pane(Shipped(), antInstalled: false);
        var row = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        Assert.False(pane.CanSignIn, "the CLI is what makes signing in possible");
        Assert.False(row.UseSignIn, "defaulting into a half that cannot be used is a dead end");
        Assert.True(row.TakesKey, "the usable half must be the one on screen");
    }

    /// <summary>
    /// The sign-in half is still REACHABLE when the CLI is missing — the segment still switches to it,
    /// and it explains what to install. Hiding it would leave "how do I sign in?" unanswerable from the
    /// pane that exists to answer it.
    /// </summary>
    [Fact]
    public void The_sign_in_half_explains_itself_when_the_cli_is_missing()
    {
        var pane = Pane(Shipped(), antInstalled: false);
        var row = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        row.UseApiKey = false;

        Assert.True(row.IsSignIn);
        Assert.False(row.SignInAvailable);
        Assert.Equal("Not signed in", row.Signal);

        // The dead end has to carry a next step, not just a greyed-out button.
        Assert.Contains("not installed", pane.SignInHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anthropic-cli", pane.SignInInstallHint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With the CLI present and no key, signing in IS the recommended path.</summary>
    [Fact]
    public void With_the_cli_present_and_no_key_the_row_opens_on_sign_in()
    {
        var pane = Pane(Shipped(), antInstalled: true);
        var row = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        Assert.True(pane.CanSignIn);
        Assert.True(row.UseSignIn);
        Assert.True(row.IsSignIn);
    }

    /// <summary>A stored key means the user meant to use it, so that row opens on API KEY.</summary>
    [Fact]
    public void A_stored_key_opens_the_row_on_the_key_half()
    {
        var options = Shipped();
        var factory = new StrategyCodegenClientFactory(
            () => new HttpClient(),
            options,
            id => id == "anthropic" ? "sk-ant-stored" : null,
            oauth: new AnthropicOAuthCli(resolveOnPath: name => name == "ant" ? @"C:\tools\ant.exe" : null));

        var builder = new AiStrategyBuilder(
            factory,
            new StrategyCodegenOrchestrator(new RoslynStrategyCompiler(), logger: null),
            StrategyContextPack.Load(),
            options);

        var pane = new AiProviderSettingsViewModel(
            builder, Microsoft.Extensions.Options.Options.Create(options), new StoredKeys("anthropic"));

        var row = Assert.Single(pane.Providers, r => r.SupportsSignIn);

        Assert.True(row.HasKey);
        Assert.True(row.TakesKey);
        Assert.False(row.IsSignIn);
    }

    private sealed class StoredKeys(params string[] ids) : IAiKeyStore
    {
        private readonly HashSet<string> _ids = new(ids, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> ConfiguredProviders => _ids;

        public bool HasKey(string providerId) => _ids.Contains(providerId);

        public void Set(string providerId, string apiKey) => _ids.Add(providerId);

        public void Remove(string providerId) => _ids.Remove(providerId);
    }
}
