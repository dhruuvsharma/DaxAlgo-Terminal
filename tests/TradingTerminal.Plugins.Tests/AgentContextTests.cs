using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What each agent is sent (#48).
///
/// <para>The loop first carried a shared transcript, which is the obvious design and wrong twice over:
/// the bill grows quadratically with turns, and every agent reads every other agent's output including
/// stale copies of the code. These pin the artifact model that replaced it — the token property AND the
/// correctness property, because here they are the same property.</para>
/// </summary>
public sealed class AgentContextTests
{
    private static StrategyFile File(string name, string body) => new(name, body);

    private static AgentContext Full() => new(
        Brief: "fade order-flow imbalance at the touch",
        Spec: "SPEC: fade imbalance on ES, 1-minute bars, stop at 2 ATR.",
        Maths: "MATHS: imbalance = (bid - ask) / (bid + ask), EMA over 20 bars.",
        Files: [File("Kernel.cs", "public sealed class K { }")],
        Findings: [new VerificationFinding("draw.blank", "Draw emitted nothing.", "Open a panel and draw.")]);

    // ── each agent gets what it acts on, and not the rest ───────────────────────────────────────

    [Fact]
    public void TheInterviewerSeesOnlyTheBrief()
    {
        var text = Full().ComposeFor(AgentRole.Interviewer);

        text.Should().Contain("fade order-flow imbalance");
        text.Should().NotContain("MATHS");
        text.Should().NotContain("CURRENT CODE");
    }

    [Fact]
    public void TheCoderSeesTheSpecAndTheMaths()
    {
        var text = Full().ComposeFor(AgentRole.Coder);

        text.Should().Contain("SPECIFICATION").And.Contain("MATHEMATICS");
    }

    [Fact]
    public void ThePainterSeesTheCodeAndNotTheProse()
    {
        // It must draw what the code already computes, not re-derive intent from the spec. Handing it
        // the specification invites it to add logic, which is the Coder's job and the Coder's score.
        var text = Full().ComposeFor(AgentRole.Painter);

        text.Should().Contain("CURRENT CODE");
        text.Should().NotContain("SPECIFICATION");
        text.Should().NotContain("MATHEMATICS");
    }

    [Fact]
    public void TheFixerSeesTheCodeAndTheDiagnosticsAndNothingElse()
    {
        // Deliberately no specification. The remedy says what to change; a Fixer that can see the
        // original intent starts redesigning instead of repairing, and re-opens rungs that had passed.
        var text = Full().ComposeFor(AgentRole.Fixer);

        text.Should().Contain("CURRENT CODE").And.Contain("WHAT FAILED").And.Contain("draw.blank");
        text.Should().NotContain("SPECIFICATION");
    }

    [Fact]
    public void TheFixerIsGivenTheRemedyNotJustTheFault()
    {
        Full().ComposeFor(AgentRole.Fixer).Should().Contain("Open a panel and draw");
    }

    [Fact]
    public void TheReviewerSeesBothIntentAndImplementation()
    {
        // Its whole job is comparing them.
        var text = Full().ComposeFor(AgentRole.Reviewer);

        text.Should().Contain("SPECIFICATION").And.Contain("CURRENT CODE");
    }

    [Fact]
    public void AnEmptyArtifactIsOmittedRatherThanSentAsAHeading()
    {
        // A heading with nothing under it spends tokens to say "there is nothing here".
        var text = new AgentContext("brief").ComposeFor(AgentRole.Coder);

        text.Should().NotContain("MATHEMATICS");
        text.Should().NotContain("CURRENT CODE");
    }

    [Fact]
    public void TheSpecFallsBackToTheBriefBeforeThereIsOne()
    {
        new AgentContext("just build me something").ComposeFor(AgentRole.Coder)
            .Should().Contain("just build me something");
    }

    // ── replacement, not accumulation ───────────────────────────────────────────────────────────

    [Fact]
    public void CodeIsReplacedSoNoStaleVersionSurvives()
    {
        // Models repair the wrong copy when an old one is in context. That is a correctness failure
        // before it is a cost one.
        var context = new AgentContext("b")
            .With(AgentRole.Coder, "reply", [File("Kernel.cs", "VERSION_ONE")])
            .With(AgentRole.Fixer, "reply", [File("Kernel.cs", "VERSION_TWO")]);

        var text = context.ComposeFor(AgentRole.Fixer);

        text.Should().Contain("VERSION_TWO");
        text.Should().NotContain("VERSION_ONE");
    }

    [Fact]
    public void OnlyTheCurrentVerdictIsCarried()
    {
        // Older findings describe code that no longer exists, and sending them invites a repair of a
        // fault that was already fixed.
        var stale = new VerificationReport([VerificationStep.Fail(
            VerificationRung.Compile, new VerificationFinding("old.fault", "gone now", "n/a"))]);
        var current = new VerificationReport([VerificationStep.Fail(
            VerificationRung.DrawProbe, new VerificationFinding("new.fault", "the real one", "fix this"))]);

        var text = new AgentContext("b", Files: [File("K.cs", "code")])
            .With(stale)
            .With(current)
            .ComposeFor(AgentRole.Fixer);

        text.Should().Contain("new.fault");
        text.Should().NotContain("old.fault");
    }

    [Fact]
    public void APassingVerdictClearsTheFindings()
    {
        var context = new AgentContext("b", Files: [File("K.cs", "code")])
            .With(new VerificationReport([VerificationStep.Fail(
                VerificationRung.Compile, new VerificationFinding("x.y", "wrong", "fix"))]))
            .With(new VerificationReport([VerificationStep.Pass(VerificationRung.Compile)]));

        context.Findings.Should().BeNull();
        context.ComposeFor(AgentRole.Fixer).Should().NotContain("WHAT FAILED");
    }

    // ── the property the whole design exists for ────────────────────────────────────────────────

    [Fact]
    public void ContextDoesNotGrowWithTheNumberOfTurns()
    {
        // The guard. A transcript would grow every turn and every turn would re-send all of it, so a
        // long run bills quadratically for its own history. Artifacts are replaced, so what a turn costs
        // depends on the size of the work and not on how long it took to get there.
        var context = new AgentContext("brief");
        var sizes = new List<int>();

        for (var turn = 0; turn < 25; turn++)
        {
            context = context
                .With(AgentRole.Coder, $"reply number {turn} with a good deal of explanatory prose in it",
                      [File("Kernel.cs", $"public sealed class K {{ /* revision {turn} */ }}")])
                .With(new VerificationReport([VerificationStep.Fail(
                    VerificationRung.DrawProbe, new VerificationFinding("draw.blank", "nothing", "draw"))]));

            sizes.Add(context.ComposeFor(AgentRole.Fixer).Length);
        }

        // Within a hair of constant: the only variation is the revision number's digit count.
        (sizes.Max() - sizes.Min()).Should().BeLessThan(20);
        sizes[^1].Should().BeLessThan(sizes[0] + 20);
    }

    [Fact]
    public void ATranscriptWouldHaveGrownWhereThisDoesNot()
    {
        // States the comparison the design rests on, so the claim is checked rather than asserted in a
        // comment. Same twenty replies: concatenated they grow without bound; as artifacts they do not.
        var replies = Enumerable.Range(0, 20)
            .Select(i => $"turn {i}: a reply of roughly realistic length, repeated to stand in for real output.")
            .ToArray();

        var transcript = string.Join("\n", replies).Length;

        var context = new AgentContext("brief");
        foreach (var reply in replies)
            context = context.With(AgentRole.Coder, reply, [File("K.cs", "public sealed class K { }")]);

        context.ComposeFor(AgentRole.Fixer).Length.Should().BeLessThan(transcript / 4);
    }
}
