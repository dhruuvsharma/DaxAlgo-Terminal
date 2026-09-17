using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Waiting for a subscription's window to reopen, rather than reporting the build as broken.
///
/// <para>A plan is metered per WINDOW, not per token: when it is spent the CLI refuses and says when it
/// reopens, and the very same request succeeds unchanged an hour later. A four-hour swarm crosses a
/// five-hour window often enough that not handling it means never finishing one.</para>
///
/// <para><b>The risk being tested is the sleep, not the retry.</b> A wrapper that mistakes a real error
/// for a closed window sleeps for ever on something no amount of waiting will fix, so the cases that
/// matter most here are the ones it must NOT wait on.</para>
/// </summary>
public sealed class WaitsOutTheUsageLimitTests
{
    private sealed class Answers(params StrategyCodegenResponse[] replies) : IStrategyCodegenClient
    {
        private int _call;

        public string ProviderId => "claude-cli";
        public string DisplayName => "Claude Code";
        public bool IsAvailable => true;

        public int Calls => _call;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            Task.FromResult(replies[Math.Min(_call++, replies.Length - 1)]);
    }

    private static StrategyCodegenRequest Request() =>
        new("PACK", [new CodegenMessage(CodegenRole.User, "build it")], "role");

    private static WaitsOutTheUsageLimit Wrap(
        IStrategyCodegenClient inner, List<TimeSpan> slept, List<string>? said = null) =>
        new(inner,
            say: line => said?.Add(line),
            sleep: (span, _) => { slept.Add(span); return Task.CompletedTask; });

    [Theory]
    [InlineData("Claude usage limit reached. Your limit will reset at 3pm.")]
    [InlineData("5-hour limit reached ∙ resets 3pm")]
    [InlineData("You have exceeded your quota for this window.")]
    [InlineData("API error 429: rate limit exceeded")]
    public async Task A_closed_window_is_waited_out_and_the_request_sent_again(string refusal)
    {
        var inner = new Answers(
            StrategyCodegenResponse.Fail(refusal),
            StrategyCodegenResponse.Ok([new StrategyFile("Unit.cs", "class Unit { }")], "done"));

        var slept = new List<TimeSpan>();
        var response = await Wrap(inner, slept).GenerateAsync(Request());

        Assert.True(response.Success);
        Assert.Equal(2, inner.Calls);
        Assert.Single(slept);
    }

    [Theory]
    [InlineData("Could not start claude.")]
    [InlineData("Claude Code exited 1: unknown flag --effort")]
    [InlineData("The model returned no message content.")]
    [InlineData("")]
    public async Task A_real_failure_is_returned_at_once(string error)
    {
        // THE CASE THAT MATTERS MOST. Waiting on something no amount of time fixes turns a two-second
        // failure into an unattended sleep, and an unrecognised refusal must therefore fail fast.
        var inner = new Answers(StrategyCodegenResponse.Fail(error));
        var slept = new List<TimeSpan>();

        var response = await Wrap(inner, slept).GenerateAsync(Request());

        Assert.False(response.Success);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(slept);
    }

    [Fact]
    public async Task A_success_is_not_slept_on()
    {
        var inner = new Answers(StrategyCodegenResponse.Ok([], "fine"));
        var slept = new List<TimeSpan>();

        Assert.True((await Wrap(inner, slept).GenerateAsync(Request())).Success);
        Assert.Empty(slept);
    }

    [Fact]
    public async Task It_gives_up_rather_than_waiting_for_ever()
    {
        // A window that never reopens, or a message this misreads, must end the run rather than sleep
        // through the rest of the day.
        var inner = new Answers(StrategyCodegenResponse.Fail("usage limit reached"));
        var slept = new List<TimeSpan>();

        var response = await Wrap(inner, slept).GenerateAsync(Request());

        Assert.False(response.Success);
        Assert.Equal(WaitsOutTheUsageLimit.MaximumWaits, slept.Count);
    }

    [Fact]
    public void The_reset_time_in_the_message_is_used_when_there_is_one()
    {
        // The CLI knows something this code cannot work out. Ignoring it would either hammer a window
        // that is still closed or sleep long past one that reopened.
        var wrapper = Wrap(new Answers(), []);
        var noon = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

        var wait = wrapper.WaitFor("usage limit reached ∙ resets 3pm", noon);

        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMinutes, 179d, 182d);
    }

    [Fact]
    public void A_reset_time_already_past_today_means_tomorrow()
    {
        var wrapper = Wrap(new Answers(), []);
        var evening = new DateTimeOffset(2026, 9, 12, 22, 0, 0, TimeSpan.Zero);

        var wait = wrapper.WaitFor("usage limit reached ∙ resets 3pm", evening);

        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalHours, 16.9d, 17.1d);
    }

    [Fact]
    public void The_machine_readable_reset_wins()
    {
        // Claude Code's stream carries a rate_limit_event with resetsAt as a unix timestamp — measured
        // on a live probe — and a number the other end computed beats anything read out of its prose.
        var wrapper = Wrap(new Answers(), []);
        var noon = DateTimeOffset.FromUnixTimeSeconds(1789213200L).AddHours(-2);

        var wait = wrapper.WaitFor(
            "usage limit reached {\"resetsAt\":1789213200,\"rateLimitType\":\"five_hour\"}", noon);

        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMinutes, 120.9d, 121.1d);
    }

    [Fact]
    public void A_relative_reset_is_read_too()
    {
        var wrapper = Wrap(new Answers(), []);

        var wait = wrapper.WaitFor("rate limit exceeded, try again in 42 minutes");

        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMinutes, 42.9d, 43.1d);
    }
}
