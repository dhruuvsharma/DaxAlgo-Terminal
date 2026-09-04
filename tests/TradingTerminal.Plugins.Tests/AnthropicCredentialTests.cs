using System.IO;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The two ways of authenticating to the Anthropic API, and they are not interchangeable header values.
///
/// <para>An API key goes on <c>x-api-key</c>. An OAuth access token goes on
/// <c>Authorization: Bearer</c> <b>and</b> needs <c>anthropic-beta: oauth-2025-04-20</c> —
/// <c>/v1/messages</c> rejects the token without it. Sending an OAuth token as <c>x-api-key</c> fails
/// auth with nothing in the message to say why, which is the mistake these pin.</para>
/// </summary>
public sealed class AnthropicCredentialTests
{
    private static HttpRequestMessage Request() =>
        new(HttpMethod.Post, "https://api.anthropic.com/v1/messages");

    [Fact]
    public async Task An_api_key_goes_on_the_api_key_header()
    {
        using var request = Request();

        (await AnthropicCredential.Key("sk-ant-test").ApplyAsync(request)).Should().BeTrue();

        request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("sk-ant-test");
        request.Headers.Contains("Authorization").Should().BeFalse();
        request.Headers.Contains("anthropic-beta").Should().BeFalse("a key is not the OAuth path");
    }

    [Fact]
    public async Task An_oauth_token_goes_on_bearer_with_the_beta_header()
    {
        using var request = Request();

        var credential = AnthropicCredential.OAuth(_ => Task.FromResult<string?>("oauth-token"));
        (await credential.ApplyAsync(request)).Should().BeTrue();

        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("oauth-token");
        request.Headers.GetValues("anthropic-beta").Should()
            .ContainSingle().Which.Should().Be(AnthropicCredential.OAuthBeta);

        request.Headers.Contains("x-api-key").Should()
            .BeFalse("an OAuth token sent as a key fails auth with nothing to explain it");
    }

    /// <summary>
    /// The token is read PER REQUEST. Captured once it would authenticate for a while and then start
    /// failing on a session nobody had touched, because an OAuth access token is short-lived.
    /// </summary>
    [Fact]
    public async Task The_token_is_re_read_on_every_request()
    {
        var issued = 0;
        var credential = AnthropicCredential.OAuth(_ => Task.FromResult<string?>($"token-{++issued}"));

        using var first = Request();
        using var second = Request();
        await credential.ApplyAsync(first);
        await credential.ApplyAsync(second);

        first.Headers.Authorization!.Parameter.Should().Be("token-1");
        second.Headers.Authorization!.Parameter.Should().Be("token-2");
    }

    /// <summary>Nobody signed in is a refusal to send, not an unauthenticated request that 401s.</summary>
    [Fact]
    public async Task A_missing_token_refuses_rather_than_sending_unauthenticated()
    {
        using var request = Request();

        var credential = AnthropicCredential.OAuth(_ => Task.FromResult<string?>(null));

        (await credential.ApplyAsync(request)).Should().BeFalse();
        request.Headers.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task A_blank_key_refuses_too()
    {
        using var request = Request();
        (await AnthropicCredential.Key("   ").ApplyAsync(request)).Should().BeFalse();
        (await AnthropicCredential.Key(null).ApplyAsync(request)).Should().BeFalse();
    }

    /// <summary>
    /// Availability is a cheap predicate, separate from fetching a token: the provider picker asks on
    /// every redraw and must not pay a process launch to decide whether to grey out a row.
    /// </summary>
    [Fact]
    public void Availability_is_answered_without_fetching_a_token()
    {
        var fetched = false;

        var unavailable = AnthropicCredential.OAuth(
            _ => { fetched = true; return Task.FromResult<string?>("t"); },
            available: () => false);

        unavailable.IsConfigured.Should().BeFalse();
        fetched.Should().BeFalse("asking whether a CLI is installed must not launch it");

        AnthropicCredential.OAuth(_ => Task.FromResult<string?>("t"), available: () => true)
            .IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void The_two_paths_are_distinguishable_so_the_pane_can_say_which_to_fix()
    {
        AnthropicCredential.OAuth(_ => Task.FromResult<string?>("t")).IsOAuth.Should().BeTrue();
        AnthropicCredential.Key("sk-ant-test").IsOAuth.Should().BeFalse();
    }

    /// <summary>
    /// The signed-in provider is LISTED even when the CLI is missing, and not available.
    ///
    /// <para>Listed, because the settings pane's whole job is showing providers you could have — hiding
    /// it would leave "sign in" undiscoverable. Not available, because the picker must not offer a
    /// provider whose first request would fail.</para>
    /// </summary>
    [Fact]
    public void The_signed_in_provider_is_listed_but_unavailable_without_the_cli()
    {
        var factory = new StrategyCodegenClientFactory(
            () => new HttpClient(),
            new TradingTerminal.Core.Configuration.AiCodegenOptions(),
            _ => null,
            oauth: new AnthropicOAuthCli(resolveOnPath: _ => null, searchDirectories: () => []));

        var signedIn = factory.BuildAll()
            .SingleOrDefault(c => c.ProviderId == AnthropicCodegenClient.SignInProviderId);

        signedIn.Should().NotBeNull("the pane cannot offer a sign-in for a provider it never lists");
        signedIn!.IsAvailable.Should().BeFalse("nothing can sign in without the CLI installed");
    }

    /// <summary>With the CLI present it becomes selectable, without anything having launched it.</summary>
    [Fact]
    public void It_becomes_available_once_the_cli_is_on_path()
    {
        var factory = new StrategyCodegenClientFactory(
            () => new HttpClient(),
            new TradingTerminal.Core.Configuration.AiCodegenOptions(),
            _ => null,
            oauth: new AnthropicOAuthCli(
                resolveOnPath: name => name == "ant" ? @"C:	oolsnt.exe" : null,
                searchDirectories: () => []));

        factory.BuildAll()
            .Single(c => c.ProviderId == AnthropicCodegenClient.SignInProviderId)
            .IsAvailable.Should().BeTrue();
    }
    /// <summary>
    /// The id follows the credential, because everything downstream routes on it.
    ///
    /// <para>It was the constant "anthropic" whatever the credential, so the signed-in client
    /// introduced itself as the keyed one: the settings pane looked for the sign-in provider by id,
    /// found none, folded nothing, and listed Anthropic TWICE with both halves asking for an API key.
    /// The test above did not catch it because it matched on DisplayName — the label on the bug.</para>
    /// </summary>
    [Fact]
    public void The_provider_id_says_which_credential_it_carries()
    {
        var http = new HttpClient();

        new AnthropicCodegenClient(http, "", "claude-opus-5", AnthropicCredential.Key("sk-ant-test"))
            .ProviderId.Should().Be(AnthropicCodegenClient.KeyProviderId);

        new AnthropicCodegenClient(
                http, "", "claude-opus-5",
                AnthropicCredential.OAuth(_ => Task.FromResult<string?>("t")))
            .ProviderId.Should().Be(AnthropicCodegenClient.SignInProviderId);

        AnthropicCodegenClient.KeyProviderId.Should().NotBe(AnthropicCodegenClient.SignInProviderId);
    }

    /// <summary>
    /// The signed-OUT output of a real `ant auth status`, captured from the CLI on 2026-09-03.
    ///
    /// <para>Verbatim, because the defect was inventing what it says. The parser looked for "not logged
    /// in" and "no active"; the real text contains NEITHER, so it answered "signed in" for a machine
    /// that was not — and the pane then offered a provider whose every request would fail with nothing
    /// to explain it. The exit code is 0 in both states, which the CLI's own docs warn about.</para>
    /// </summary>
    private const string SignedOut = """
        Active profile:  default (fallback - no active_config set)
        Config dir:      C:\Temp\ant-empty-cfg
        Credentials:     C:\Temp\ant-empty-cfg\credentials\default.json

        Credentials
          (profile "default" not configured - run `ant auth login` to set it up)

        Base URL
          (active) * SDK default                                    https://api.anthropic.com
        """;

    /// <summary>The signed-IN output from the same run, with the token and org identifiers removed.</summary>
    private const string SignedIn = """
        Active profile:  default (from active_config file)
        Config dir:      C:\Users\me\AppData\Roaming\Anthropic

        Credentials
          Logged in to Example Org as someone@example.com
          (active) * Profile (user_oauth) [via active_config]       sk-ant-oat01-...
                       scope:          user:developer user:inference user:profile

        Base URL
          (active) * SDK default                                    https://api.anthropic.com
        """;

    [Fact]
    public void A_signed_out_cli_is_not_read_as_signed_in()
    {
        AnthropicOAuthCli.ReportsSignedIn(SignedOut).Should().BeFalse(
            "the real signed-out text matches neither negative the parser used to look for, which is "
            + "how it reported a signed-in machine that was not");
    }

    [Fact]
    public void A_signed_in_cli_is_recognised()
    {
        AnthropicOAuthCli.ReportsSignedIn(SignedIn).Should().BeTrue();
    }

    [Fact]
    public void Nothing_at_all_is_not_signed_in()
    {
        AnthropicOAuthCli.ReportsSignedIn(null).Should().BeFalse();
        AnthropicOAuthCli.ReportsSignedIn(string.Empty).Should().BeFalse();
        AnthropicOAuthCli.ReportsSignedIn("   ").Should().BeFalse();
    }

    /// <summary>
    /// The CLI is found where its installer put it, not only on PATH.
    ///
    /// <para>`go install` is the documented route on Windows and drops the binary in
    /// <c>%USERPROFILE%\go\bin</c>, which is not on PATH by default — so a user who had installed the
    /// CLI and signed in successfully was still told it was not installed.</para>
    /// </summary>
    [Fact]
    public void The_cli_is_found_where_go_install_puts_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "daxalgo-ant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? "ant.exe" : "ant");
        File.WriteAllText(exe, string.Empty);

        try
        {
            new AnthropicOAuthCli(resolveOnPath: _ => null, searchDirectories: () => [directory])
                .IsInstalled.Should().BeTrue("it is where the installer puts it, PATH or not");

            new AnthropicOAuthCli(resolveOnPath: _ => null, searchDirectories: () => [])
                .IsInstalled.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
