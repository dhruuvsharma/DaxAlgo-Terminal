using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The role instructions (#48), and the composition that carries them.
///
/// <para>The wording will need iteration against real models; the <b>structure</b> will not, and it is
/// the half that is expensive to get wrong. These pin the structure.</para>
/// </summary>
public sealed class AgentPromptTests
{
    private static IEnumerable<AgentRole> Roles => Enum.GetValues<AgentRole>();

    [Fact]
    public void EveryRoleHasAnInstruction()
    {
        foreach (var role in Roles)
            AgentPrompts.For(role).Should().NotBeNullOrWhiteSpace($"{role} must know what it owns");
    }

    [Fact]
    public void EveryInstructionNamesItsOwnRole()
    {
        foreach (var role in Roles)
            AgentPrompts.For(role).Should().Contain(role.ToString());
    }

    [Fact]
    public void EveryInstructionSaysWhatTheAgentMustNotDo()
    {
        // The failure a split like this produces is not an agent doing its job badly — it is an agent
        // quietly doing another's, at which point the rung meant to score one is scoring two.
        foreach (var role in Roles)
        {
            // Whitespace-normalised: these are wrapped raw strings, so a phrase can straddle a line
            // break. Asserting on the layout rather than the words made this fail for the Reviewer,
            // whose "Write no code" happens to wrap between the two.
            var text = System.Text.RegularExpressions.Regex
                .Replace(AgentPrompts.For(role), @"\s+", " ")
                .ToLowerInvariant();

            text.Should().MatchRegex("do not|write no|change no|and only that|nothing else",
                $"{role} must be told where its work stops");
        }
    }

    [Fact]
    public void TheInstructionsAreDistinct()
    {
        var texts = Roles.Select(AgentPrompts.For).ToArray();
        texts.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheyStayShortBecauseTheSharedPackAlreadySaysTheRest()
    {
        // Six copies of the contracts would defeat the document that exists so there is one — and it is
        // the half that cannot drift, so duplicating it into prose is how it starts to.
        foreach (var role in Roles)
            AgentPrompts.For(role).Length.Should().BeLessThan(1400, $"{role}'s instruction is a role, not a manual");
    }

    [Fact]
    public void NoInstructionRestatesTheContracts()
    {
        // If a role prompt starts teaching the SDK, the generated surface has been forked into prose.
        foreach (var role in Roles)
        {
            var text = AgentPrompts.For(role);
            text.Should().NotContain("IRenderSurface", $"{role} must not restate the drawing contract");
            text.Should().NotContain("SetTargetPosition", $"{role} must not restate the book contract");
        }
    }

    [Fact]
    public void OnlyTheAgentsThatBuildAreExpectedToWriteCode()
    {
        // An Interviewer answering with no code block is doing its job, not failing to generate.
        AgentPrompts.WritesCode(AgentRole.Interviewer).Should().BeFalse();
        AgentPrompts.WritesCode(AgentRole.Reviewer).Should().BeFalse();

        AgentPrompts.WritesCode(AgentRole.Coder).Should().BeTrue();
        AgentPrompts.WritesCode(AgentRole.Painter).Should().BeTrue();
        AgentPrompts.WritesCode(AgentRole.Fixer).Should().BeTrue();
    }

    [Fact]
    public void ThePainterIsToldNotToChangeTheLogic()
    {
        // Otherwise a drawing turn silently repairs a strategy and the failure is never attributed to
        // whoever wrote it.
        AgentPrompts.For(AgentRole.Painter).Should().Contain("Change no trading logic");
    }

    [Fact]
    public void TheFixerIsToldToStartWithTheEarliestFailure()
    {
        // Later findings are usually consequences of the first; fixing them in order wastes turns the
        // user pays for.
        AgentPrompts.For(AgentRole.Fixer).ToLowerInvariant().Should().Contain("earliest");
    }

    // ── composition: the part that costs money if it is wrong ───────────────────────────────────

    [Fact]
    public void TheRoleTravelsSeparatelyFromTheSharedPack()
    {
        // Providers cache on an exact prefix. Appending the role to SystemContext would make every agent
        // a different prefix, so each role switch re-bills the shared pack — roughly twelve thousand
        // tokens — at full price.
        var request = new StrategyCodegenRequest(
            "SHARED PACK",
            [new CodegenMessage(CodegenRole.User, "build me something")],
            AgentPrompts.For(AgentRole.Coder));

        request.SystemContext.Should().Be("SHARED PACK");
        request.SystemContext.Should().NotContain("YOUR ROLE");
        request.RoleInstruction.Should().Contain("YOUR ROLE: Coder");
    }

    [Fact]
    public void ASingleAgentSessionCarriesNoRole()
    {
        // The in-app builder is still one conversation; nothing should have to pass a role to use it.
        new StrategyCodegenRequest("pack", []).RoleInstruction.Should().BeNull();
    }
}
