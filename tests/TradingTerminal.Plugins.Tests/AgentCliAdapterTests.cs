using FluentAssertions;
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
}
