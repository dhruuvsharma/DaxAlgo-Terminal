using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// How many questions a session is allowed to ask, and what ends the asking.
///
/// <para>Both places that governed this said "ask once, two to four questions, then write it", and
/// one of them added "do not ask twice". For a window with a book, a heatmap, a tape and a strip that
/// is not a small question and four asked once will not settle it — so the guidance was not a ceiling
/// on chattiness, it was an instruction to guess.</para>
///
/// <para>The replacement is adaptive, and adaptive without an exit is worse than a ceiling. So the
/// two halves are pinned together here: the model is told to keep asking while the answers still
/// change what gets written, AND told that the user can end the interview at any point and that it
/// must honour that immediately. <c>AuthoringActionTests</c> pins the button that sends it.</para>
/// </summary>
public sealed class AdaptiveQuestioningTests
{
    private static string Pack => StrategyContextPack.Load().Conventions;

    private static string Interviewer => AgentPrompts.For(AgentRole.Interviewer);

    [Fact]
    public void Neither_place_still_says_ask_once()
    {
        // The exact wording that made this a defect. Left anywhere, it contradicts the guidance beside
        // it, and a model handed two contradictory instructions follows the more specific one.
        foreach (var (where, text) in new[] { ("the conventions pack", Pack), ("the interviewer", Interviewer) })
        {
            text.Should().NotContain("two to four", $"{where} must no longer cap the interview");
            text.Should().NotContain("do not ask twice", $"{where} must no longer forbid a second round");
            text.Should().NotContain("Ask once", $"{where} must no longer forbid a second round");
        }
    }

    [Fact]
    public void Both_places_give_the_same_stop_condition()
    {
        // "As many as it needs" without a stop condition is an invitation to interview forever. The
        // condition is the useful part: ask while the answer would change what gets written.
        foreach (var (where, text) in new[] { ("the conventions pack", Pack), ("the interviewer", Interviewer) })
        {
            text.Should().Contain(
                "changes what", $"{where} must say what makes a question worth asking");
            text.Should().Contain(
                "stop", $"{where} must say when to stop");
        }
    }

    [Fact]
    public void Both_places_say_the_user_can_end_it()
    {
        // The other half of adaptive. A model that keeps asking after being told to build is worse
        // than one that never asked, because the user has now paid for both.
        Pack.Should().Contain("build it");
        Interviewer.Should().Contain("build it");

        // And that the assumptions come back with it — "just build it" that settles the open questions
        // invisibly leaves the user holding a unit they cannot correct.
        Pack.Should().Contain("assumed");
        Interviewer.Should().Contain("assumed");
    }

    [Fact]
    public void A_specification_awaiting_approval_is_treated_as_a_question()
    {
        // The third thing the brief asked for, and the one that is easiest to leave implicit. A turn
        // that ends with "here is what I will build, confirm it" is waiting on the user exactly as
        // "which instrument?" is, and gets the same block.
        Pack.Should().Contain("awaiting approval");
        Interviewer.Should().Contain("approval");
    }

    [Fact]
    public void The_adaptive_guidance_reaches_the_composed_prompt()
    {
        // The discipline this area keeps failing. Guidance that lives only in a file nobody appends is
        // guidance nobody follows.
        var session = new StrategyCodegenOrchestrator(
                new RoslynStrategyCompiler(), logger: null,
                skills: StrategySkillLibrary.Load(), pack: StrategyContextPack.Load())
            .CreateSession(
                new Silent(), StrategyContextPack.Load().SystemPrompt, "q", "Q", maxFixAttempts: 0,
                profile: StrategyBuildProfile.For(StrategyBuildEffort.Deep));

        var composed = session.PrepareFor("an order book depth ladder with a liquidity heatmap");

        composed.Should().Contain("as many as the job needs");
        composed.Should().Contain("awaiting approval");
        composed.Should().NotContain("two to four");
    }

    private sealed class Silent : IStrategyCodegenClient
    {
        public string ProviderId => "silent";
        public string DisplayName => "Silent";
        public bool IsAvailable => false;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
