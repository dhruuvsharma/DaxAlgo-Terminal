using FluentAssertions;
using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The third table of vendor facts: the installed-CLI providers.
///
/// <para>These matter more than their two-row size suggests, because they are the only providers that
/// need <b>no API key</b> — the user's existing Claude Code or Codex sign-in does the work. For anyone
/// who has one installed, this is the shortest path from "I want to vibe-code a strategy" to a
/// generated unit, and nothing checked the argv it builds.</para>
///
/// <para>Every expectation below was read out of the CLIs' own <c>--help</c> on 2026-08-31, so this
/// file and the adapter table are two independent statements of the same fact. A CLI that changes its
/// flags will not break this file — only a live run can catch that — but an edit to the table that
/// contradicts what the CLI documented will.</para>
/// </summary>
public sealed class AgentCliAdapterTests
{
    /// <summary>
    /// What <c>claude --effort</c> accepts, verbatim from its help: "(low, medium, high, xhigh, max)".
    /// </summary>
    private static readonly string[] ClaudeEffortLevels = ["low", "medium", "high", "xhigh", "max"];

    [Fact]
    public void Every_effort_the_app_can_send_is_one_the_cli_accepts()
    {
        // An effort the CLI rejects is not a degraded run, it is a failed one -- the process exits on
        // the argument before a token is generated. The app's ladder and the CLI's accepted set have to
        // be the same set, not merely overlapping.
        var sent = Enum.GetValues<CodegenEffort>()
            .Select(effort => effort.Wire())
            .Where(wire => wire is not null)
            .ToArray();

        sent.Should().BeEquivalentTo(
            ClaudeEffortLevels,
            "the effort ladder and `claude --effort` must agree exactly -- anything the app can send "
            + "and the CLI does not know fails the run outright");
    }

    [Fact]
    public void Codex_is_never_sent_an_effort_flag()
    {
        // It has none: reasoning is configured through its own config file. Sending one would be an
        // unrecognised argument, which is a hard failure rather than a setting quietly ignored.
        AgentCliAdapter.Codex.EffortFlag.Should().BeNull();

        var argv = AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.Max);

        argv.Should().NotContain("--effort");
        argv.Should().NotContain("max");
    }

    [Fact]
    public void Codex_keeps_every_flag_in_front_of_the_stdin_marker()
    {
        // `codex exec --help`: "If not provided as an argument (or if `-` is used), instructions are
        // read from stdin." So the trailing `-` is what makes the piped prompt the prompt -- and
        // anything after it would be read as arguments to it rather than as options.
        var argv = AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.Default, cliProfile: "work");

        argv.Should().EndWith("-", "the stdin marker has to stay last");
        argv.Count(a => a == "-").Should().Be(1, "one marker, or the prompt source is ambiguous");

        var marker = argv.ToList().IndexOf("-");
        argv.ToList().IndexOf("-m").Should().BeLessThan(marker);
        argv.ToList().IndexOf("--profile").Should().BeLessThan(marker);
    }

    [Fact]
    public void Claude_streams_only_in_the_combination_its_cli_allows()
    {
        // Three constraints from `claude --help`, and the flags are useless apart:
        //   --output-format          "only works with --print"
        //   --include-partial-messages / --verbose   "(--print and --output-format=stream-json)"
        // Sending stream-json without -p is rejected, so the print flag is not optional here.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor("opus", CodegenEffort.High, stream: true);

        argv.Should().Contain("-p");
        argv.Should().ContainInOrder("--output-format", "stream-json");
        argv.Should().Contain("--include-partial-messages");
        argv.Should().Contain("--verbose");
    }

    [Fact]
    public void A_non_streaming_claude_run_asks_for_no_streaming_flags()
    {
        // The fallback path. stream-json changes the OUTPUT SHAPE, so leaking these flags into a
        // one-shot run would hand the plain-text parser a JSONL document.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor("opus", CodegenEffort.High);

        argv.Should().Contain("-p");
        argv.Should().NotContain("--output-format");
        argv.Should().NotContain("--include-partial-messages");
    }

    [Fact]
    public void An_unset_model_or_effort_leaves_the_cli_on_its_own_defaults()
    {
        // A blank model must not become an empty argument: `--model ""` is not "use the default", it is
        // a model named empty string, and the CLI rejects it.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor(null, CodegenEffort.Default);

        argv.Should().NotContain("--model");
        argv.Should().NotContain("--effort");
        argv.Should().NotContain(string.Empty);
    }

    // -- the fourth table: the curated model lists ------------------------------------------------

    [Fact]
    public void The_configured_model_is_always_offered_and_offered_first()
    {
        // The contract the picker depends on. A configured model missing from its own dropdown reads as
        // "that setting is invalid" -- and the ids most at risk are exactly the ones not on any curated
        // list, because those are the new ones somebody typed in deliberately.
        var offered = AiModelCatalog.Offer("anthropic", "claude-something-unreleased");

        offered.Should().HaveElementAt(0, "claude-something-unreleased");
        offered.Should().Contain(AiModelCatalog.For("anthropic"),
            "the curated list is added to, not replaced");

        // Already-listed ids are promoted rather than duplicated.
        var promoted = AiModelCatalog.Offer("anthropic", "claude-sonnet-5");
        promoted.Should().HaveElementAt(0, "claude-sonnet-5");
        promoted.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_curated_anthropic_list_still_names_the_current_flagship()
    {
        // It went stale once. The list called claude-opus-4-8 the "most capable Opus tier" while
        // appsettings.json was already shipping a provider configured for claude-opus-5, so the picker
        // omitted the very model the app itself pointed at. Free text meant nobody was blocked, which
        // is precisely why it could sit there unnoticed.
        var models = AiModelCatalog.For("anthropic");

        models.Should().Contain("claude-opus-5");
        models.Should().OnlyHaveUniqueItems();
        models.Should().NotContain(string.Empty);

        // The CLI provider shares the list, so the API and the installed CLI never offer different
        // menus for the same vendor.
        AiModelCatalog.For("claude-cli").Should().BeEquivalentTo(models);
    }

    [Fact]
    public void Providers_without_a_curated_list_offer_nothing_rather_than_a_guess()
    {
        // Deliberate: these all expose a models endpoint, and the picker has a "refresh from provider"
        // that asks. A shipped guess would be stale within weeks -- the appsettings notes record one
        // stealth id that was free for exactly a week.
        foreach (var id in (string[])["openai", "deepseek", "xai", "openrouter", "ollama", "codex-cli"])
        {
            AiModelCatalog.For(id).Should().BeEmpty(
                "{0} should be asked rather than guessed at", id);
        }
    }

    [Fact]
    public void Every_adapter_names_a_bare_executable_rather_than_a_path()
    {
        // Resolved off PATH, so the app finds whatever the user installed rather than a location we
        // guessed. A path here would work on the machine it was written on and nowhere else.
        foreach (var adapter in AgentCliAdapter.All)
        {
            adapter.Executable.Should().NotBeNullOrWhiteSpace();
            adapter.Executable.Should().NotContain("/");
            adapter.Executable.Should().NotContain("\\");
            adapter.Executable.Should().NotEndWith(".exe", "the extension is the platform's business");

            adapter.ProviderId.Should().NotBeNullOrWhiteSpace();
            adapter.Arguments.Should().NotBeEmpty("a CLI invoked bare would open its interactive mode "
                + "and hang the turn until the timeout");
        }

        AgentCliAdapter.All.Select(a => a.ProviderId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryRedirectedStreamIsUtf8()
    {
        // All three, and the OUTPUT half is the one that was missing. A redirected pipe inherits the
        // Windows console code page, so a model writing an en dash sends back E2 80 93 and the client
        // decoded it as CP437: the generated source then held the literal string "ΓÇô".
        //
        // It compiled. It cleared every rung. It reached a window and showed
        // "77671.75 ΓÇô 77671.75" to a user, which is where it was finally seen -- in a
        // screenshot of a unit that had passed everything. The input half was fixed long ago because
        // Codex REJECTS invalid UTF-8 and fails loudly; nothing rejects a corrupted reply.
        var client = new AgentCliCodegenClient(AgentCliAdapter.ClaudeCode);
        var psi = client.ProcessFor("claude", stream: false);

        psi.StandardInputEncoding.Should().BeOfType<UTF8Encoding>();
        psi.StandardOutputEncoding.Should().BeOfType<UTF8Encoding>(
            "a reply decoded as the console code page reaches the user as mojibake and nothing rejects it");
        psi.StandardErrorEncoding.Should().BeOfType<UTF8Encoding>();
    }
}
