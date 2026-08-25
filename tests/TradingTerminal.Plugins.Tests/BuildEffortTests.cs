using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Effort is the selector between one conversation and the six agents (#48).
///
/// <para>It was chosen because it already means what the decision is about. A user who picks Deep or Max
/// has said correctness over cost, and the agent split is exactly that trade. Reusing the dial means no
/// second toggle to explain, and no way to ask for extreme quality and quietly get the cheap path.</para>
/// </summary>
public sealed class BuildEffortTests
{
    private static StrategyBuildProfile Profile(StrategyBuildEffort effort) =>
        StrategyBuildProfile.For(effort);

    [Theory]
    [InlineData(StrategyBuildEffort.Quick, false)]
    [InlineData(StrategyBuildEffort.Standard, false)]
    [InlineData(StrategyBuildEffort.Deep, true)]
    [InlineData(StrategyBuildEffort.Max, true)]
    public void TheAgentsAreWhatTheUpperEffortsBuy(StrategyBuildEffort effort, bool expected)
    {
        Profile(effort).UseAgents.Should().Be(expected);
    }

    [Fact]
    public void EffortNeverGoesBackwards()
    {
        // Each level must be at least as thorough as the one below it, or the dial stops meaning what
        // the user reads it to mean.
        var levels = new[]
        {
            StrategyBuildEffort.Quick, StrategyBuildEffort.Standard,
            StrategyBuildEffort.Deep, StrategyBuildEffort.Max,
        };

        for (var i = 1; i < levels.Length; i++)
        {
            var lower = Profile(levels[i - 1]);
            var higher = Profile(levels[i]);

            higher.MaxSkills.Should().BeGreaterThanOrEqualTo(lower.MaxSkills);
            higher.MaxFixAttempts.Should().BeGreaterThanOrEqualTo(lower.MaxFixAttempts);
            higher.MaxAgentTurns.Should().BeGreaterThanOrEqualTo(lower.MaxAgentTurns);
        }
    }

    [Fact]
    public void EveryAgentRunIsVerified()
    {
        // The agents are scored by the ladder — routing them without running it would leave reliability
        // learning from nothing, and the whole split pointless.
        foreach (var effort in Enum.GetValues<StrategyBuildEffort>())
        {
            var profile = Profile(effort);
            if (profile.UseAgents) profile.Verify.Should().BeTrue($"{effort} routes agents");
        }
    }

    [Fact]
    public void AnAgentRunHasEnoughTurnsToReachEveryRoleBeforeItRepairsAnything()
    {
        // Interview, maths, code, picture, review is five turns before a single fix. A budget that could
        // not reach the Reviewer would spend the user's money and then stop short of the agent that
        // covers what verification cannot see.
        foreach (var effort in Enum.GetValues<StrategyBuildEffort>())
        {
            var profile = Profile(effort);
            if (!profile.UseAgents) continue;

            profile.MaxAgentTurns.Should().BeGreaterThan(5, $"{effort} must reach the Reviewer");
            profile.MaxAgentTurns.Should().BeGreaterThan(
                profile.MaxFixAttempts,
                "a run spends turns on roles before it spends any on repair");
        }
    }

    [Fact]
    public void TheDialAlsoCarriesHowHardTheModelThinks()
    {
        // There used to be a second dropdown for this. Two dials for one intention is a way to ask for
        // extreme quality and be quietly given the cheap setting on the other control.
        Profile(StrategyBuildEffort.Quick).Reasoning.Should().Be(CodegenEffort.Low);
        Profile(StrategyBuildEffort.Max).Reasoning.Should().Be(CodegenEffort.Max);
    }

    [Fact]
    public void ReasoningRisesWithTheDialToo()
    {
        // Same rule as every other field on the profile: a higher setting is never less thorough. Under a
        // token budget this is the cheaper direction, not the more expensive one — a failed turn pays its
        // full input and buys nothing, so getting the turn right first time costs less per delivered
        // strategy than a retry does.
        var levels = new[]
        {
            StrategyBuildEffort.Quick, StrategyBuildEffort.Standard,
            StrategyBuildEffort.Deep, StrategyBuildEffort.Max,
        };

        for (var i = 1; i < levels.Length; i++)
            ((int)Profile(levels[i]).Reasoning)
                .Should().BeGreaterThanOrEqualTo((int)Profile(levels[i - 1]).Reasoning);
    }

    [Fact]
    public void TheCheapPathIsStillCheap()
    {
        // Quick must stay quick: one skill, one fix, no review, no ladder, one conversation. If it drifts
        // upward there is nothing left for a user who wants a sketch.
        var quick = Profile(StrategyBuildEffort.Quick);

        quick.UseAgents.Should().BeFalse();
        quick.Verify.Should().BeFalse();
        quick.SelfReview.Should().BeFalse();
        quick.MaxSkills.Should().Be(1);
    }

    [Fact]
    public void TheDefaultIsStandard()
    {
        // An unrecognised value must land on the middle, not the most expensive. A configuration typo
        // should not silently start billing for six agents.
        StrategyBuildProfile.For((StrategyBuildEffort)999)
            .Should().Be(Profile(StrategyBuildEffort.Standard));
    }
}
