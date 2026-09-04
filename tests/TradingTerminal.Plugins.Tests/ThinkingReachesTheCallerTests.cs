using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The model's thinking has to survive every hop between the wire and the workspace.
///
/// <para><b>It did not, and the missing hop was invisible.</b> The client emitted ReasoningDelta, the
/// UI had a collapsed panel waiting for it, and StrategyBuildSession — which relays streamed events to
/// its caller — switched on the event type and enumerated only TextDelta and UsageUpdate. A new event
/// kind therefore fell through to nothing: no exception, no warning, no failing test, and a reasoning
/// model still showed the user a shimmer and silence for minutes. Adding the event was not the same as
/// delivering it, and only an end-to-end assertion tells those apart.</para>
///
/// <para>So this drives the REAL session against a scripted provider and asserts on what the caller
/// receives, rather than on what any one layer emits.</para>
/// </summary>
public sealed class ThinkingReachesTheCallerTests
{
    [Fact]
    public async Task A_reasoning_models_thinking_arrives_at_the_session_caller()
    {
        var provider = new ScriptedStreamingProvider(
            new CodegenEvent.ReasoningDelta("weighing the entry rule"),
            new CodegenEvent.ReasoningDelta(" against the exit"),
            new CodegenEvent.TextDelta("Here is the plan."),
            new CodegenEvent.Completed(StrategyCodegenResponse.Reply("Here is the plan.")));

        var seen = new List<CodegenEvent>();
        var session = new StrategyBuildSession(
            new RoslynStrategyCompiler(), provider, "PACK", "s", "S", maxFixAttempts: 0);

        await session.SendAsync("build me something", null, default, new Progress<CodegenEvent>(seen.Add));

        // Progress<T> posts to the captured context; in a test that is the thread pool, so give the
        // posted callbacks a moment to land rather than racing them.
        await WaitFor(() => seen.OfType<CodegenEvent.TextDelta>().Any());

        // The array overload, not the params one: Equal(a, b, "because...") would read the reason as a
        // third expected element and fail on a relay that is working perfectly.
        seen.OfType<CodegenEvent.ReasoningDelta>().Select(r => r.Text)
            .Should().Equal(["weighing the entry rule", " against the exit"],
                "the thinking is the only thing on screen while a reasoning model works");
    }

    [Fact]
    public async Task The_reply_still_arrives_alongside_it()
    {
        var provider = new ScriptedStreamingProvider(
            new CodegenEvent.ReasoningDelta("thinking"),
            new CodegenEvent.TextDelta("done"),
            new CodegenEvent.Completed(StrategyCodegenResponse.Reply("done")));

        var seen = new List<CodegenEvent>();
        var session = new StrategyBuildSession(
            new RoslynStrategyCompiler(), provider, "PACK", "s", "S", maxFixAttempts: 0);

        await session.SendAsync("brief", null, default, new Progress<CodegenEvent>(seen.Add));
        await WaitFor(() => seen.OfType<CodegenEvent.TextDelta>().Any());

        // Thinking is a side channel, not a replacement for the answer.
        seen.OfType<CodegenEvent.TextDelta>().Select(t => t.Text).Should().Equal("done");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    }

    /// <summary>Replays a fixed event sequence, so the assertion is about the relay rather than about
    /// any particular provider's wire format.</summary>
    private sealed class ScriptedStreamingProvider(params CodegenEvent[] events) : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";

        public string DisplayName => "Scripted";

        public bool IsAvailable => true;

        public string Model => "scripted-1";

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            Task.FromResult(StrategyCodegenResponse.Reply("unused"));

        public async IAsyncEnumerable<CodegenEvent> StreamAsync(
            StrategyCodegenRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
