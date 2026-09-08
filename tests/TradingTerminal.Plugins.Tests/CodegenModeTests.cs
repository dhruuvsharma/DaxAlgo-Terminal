using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The one dial: Standard runs the model at its own default, Research runs it at the highest setting
/// that model is known to still ANSWER at.
///
/// <para><b>The distinction those two words carry is the whole feature.</b> A model that accepts
/// <c>reasoning_effort</c> and then reasons until its budget is gone has produced nothing,
/// expensively — measured on z-ai/glm-5.3-free at 1,088 seconds and 95,763 output tokens for no code.
/// So "supported" is not the question and never was; "still returns something" is.</para>
/// </summary>
public sealed class CodegenModeTests
{
    // ── what each position sends ────────────────────────────────────────────────────────────────

    [Fact]
    public void Standard_sends_no_effort_parameter_at_all()
    {
        // Not "low". Sending nothing is the only setting every model accepts, including the ones that
        // predate the parameter — which is why the default position is the one that sends nothing
        // rather than the one that sends the smallest value.
        StrategyBuildProfile.For(CodegenMode.Standard, CodegenEffort.Default).Reasoning
            .Should().Be(CodegenEffort.Default);
    }

    [Fact]
    public void Research_sends_the_highest_setting_the_model_answers_at()
    {
        AiModelCatalog.ResearchEffort("anthropic", "claude-opus-5").Should().Be(CodegenEffort.Max);
        AiModelCatalog.ResearchEffort("openai", "o4").Should().Be(CodegenEffort.High);
    }

    [Fact]
    public void Research_on_a_model_that_accepts_the_setting_and_then_never_answers_falls_back()
    {
        // The measured case, and the reason ResearchEffort is not a lookup of SupportsEffort.
        AiModelCatalog.ResearchEffort("tokenrouter", "z-ai/glm-5.3-free")
            .Should().Be(CodegenEffort.Default);

        AiModelCatalog.ResearchAvailable("tokenrouter", "z-ai/glm-5.3-free").Should().BeFalse();
    }

    [Fact]
    public void The_same_model_is_recognised_whatever_gateway_it_arrives_through()
    {
        // A model id travels with a vendor prefix on one gateway and without it on another. It is the
        // same model and the same measurement, so the fallback has to apply to both.
        AiModelCatalog.ResearchEffort("openrouter", "z-ai/glm-5.3").Should().Be(CodegenEffort.Default);
        AiModelCatalog.ResearchEffort("openrouter", "some/other-model").Should().Be(CodegenEffort.High);
    }

    [Fact]
    public void A_provider_with_no_effort_knob_has_no_research_setting()
    {
        AiModelCatalog.ResearchAvailable("ollama", "llama3.1").Should().BeFalse();
        AiModelCatalog.ResearchAvailable("nvidia", "deepseek-ai/deepseek-v4-pro-0813").Should().BeFalse();
    }

    // ── the fallback is announced, not silent ───────────────────────────────────────────────────

    [Fact]
    public void The_fallback_says_what_happened_and_what_is_running_instead()
    {
        var notice = AiModelCatalog.ResearchUnavailable("tokenrouter", "z-ai/glm-5.3-free");

        notice.Should().NotBeNull();
        notice.Should().Contain("not available");
        notice.Should().Contain("z-ai/glm-5.3-free");

        // And that the build has NOT quietly become Standard: everything except the thinking setting
        // still applies. A notice that only said "unavailable" would read as "Research did nothing".
        notice.Should().Contain("still applies");
    }

    [Fact]
    public void A_model_that_can_do_research_gets_no_notice()
    {
        AiModelCatalog.ResearchUnavailable("anthropic", "claude-opus-5").Should().BeNull();
    }

    // ── the rest of Research still happens ──────────────────────────────────────────────────────

    [Fact]
    public void Research_still_buys_the_agents_and_the_full_skill_budget_when_the_thinking_falls_back()
    {
        // The half a user would otherwise lose silently. Falling back is about the reasoning parameter
        // and nothing else.
        var fallen = StrategyBuildProfile.For(
            CodegenMode.Research, AiModelCatalog.ResearchEffort("tokenrouter", "z-ai/glm-5.3-free"));

        fallen.Reasoning.Should().Be(CodegenEffort.Default);
        fallen.UseAgents.Should().BeTrue();
        fallen.SelfReview.Should().BeTrue();
        fallen.MaxSkills.Should().Be(StrategyBuildProfile.For(StrategyBuildEffort.Max).MaxSkills);
        fallen.MaxFixAttempts.Should().Be(StrategyBuildProfile.For(StrategyBuildEffort.Max).MaxFixAttempts);
    }

    [Fact]
    public void Standard_is_one_conversation_and_Research_is_the_agent_path()
    {
        StrategyBuildProfile.For(CodegenMode.Standard, CodegenEffort.Default).UseAgents.Should().BeFalse();
        StrategyBuildProfile.For(CodegenMode.Research, CodegenEffort.Max).UseAgents.Should().BeTrue();
    }

    // ── persistence, including from before the dial existed ─────────────────────────────────────

    [Theory]
    [InlineData("standard", CodegenMode.Standard)]
    [InlineData("research", CodegenMode.Research)]
    public void A_mode_round_trips_through_its_wire_value(string wire, CodegenMode expected)
    {
        CodegenModes.Parse(wire).Should().Be(expected);
        expected.Wire().Should().Be(wire);
    }

    [Theory]
    [InlineData("quick", CodegenMode.Standard)]
    [InlineData("standard", CodegenMode.Standard)]
    [InlineData("deep", CodegenMode.Research)]
    [InlineData("max", CodegenMode.Research)]
    public void The_retired_four_position_words_still_open_on_the_right_position(string old, CodegenMode expected)
    {
        // A session or a config file written before this dial existed must not silently open on
        // Standard when its owner had asked for Max.
        CodegenModes.Parse(old).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void Anything_unrecognised_is_Standard_rather_than_an_error(string? value)
    {
        CodegenModes.Parse(value).Should().Be(CodegenMode.Standard);
    }
}
