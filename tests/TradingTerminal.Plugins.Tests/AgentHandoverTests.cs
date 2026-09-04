using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The handover: a role that writes no code finishes its turn rather than ending the run.
///
/// <para><b>Every one of these starts from <c>new RoutingState()</c></b> — the literal expression the
/// authoring view-model uses — and that is the whole point of the file. <c>HasSpec: true</c> appeared
/// in twenty-eight places in this suite and in none of the product, so the entire agent path was
/// covered from one state past the gate the application could never get through. The loop routed to the
/// Interviewer on every turn for the life of a session; a user's saved session shows six briefs, six
/// interviews and no code, including the turns that said "approved, now start building".</para>
///
/// <para>Deep and Max are the two efforts that route here, so this was the top two settings of the
/// dial, and only those: Quick and Standard use the single conversation and were never affected.</para>
/// </summary>
public sealed class AgentHandoverTests
{
    private const string Code = "```csharp\n// file: X.cs\npublic sealed class X { }\n```";

    private const string SpecHandover =
        "Here is the specification. EMA(20) against EMA(50) on 1-minute bars, one position at a time, "
        + "flat on the opposite cross. I assumed the defaults for anything still open. "
        + AgentPrompts.Handover;

    private const string SpecWithQuestion = """
        Before I write it I need one thing settled.

        ```questions
        [{"id":"tf","question":"Which timeframe?","options":[{"label":"1 minute"},{"label":"5 minute"}]}]
        ```
        """;

    private static StrategyCodegenResponse Wrote() => new(
        Success: true,
        Code: "public sealed class X { }",
        RawText: Code,
        Error: null,
        Files: [new StrategyFile("X.cs", "public sealed class X { }")]);

    /// <summary>
    /// A prose reply, extracted the way a real client extracts — because the defect being pinned IS the
    /// extraction. Hard-coding Files:[] here would quietly make every fenced-sketch test vacuous.
    /// </summary>
    private static StrategyCodegenResponse Said(string text)
    {
        var files = CodegenCodeExtractor.ExtractFiles(text);
        return files.Count > 0
            ? StrategyCodegenResponse.Ok(files, text)
            : StrategyCodegenResponse.Reply(text);
    }

    private static VerificationReport Passing() => new(
    [
        VerificationStep.Pass(VerificationRung.Compile),
        VerificationStep.Pass(VerificationRung.Shape),
        VerificationStep.Pass(VerificationRung.Lifecycle),
    ]);

    /// <summary>A judge that advances the state it is handed, the way the real one advances its own.</summary>
    private static Func<IReadOnlyList<StrategyFile>, VerdictAndState> Judge(RoutingState seed)
    {
        var state = seed;
        return _ =>
        {
            state = LadderFeedback.Advance(state, Passing());
            return new VerdictAndState(Passing(), state);
        };
    }

    /// <summary>
    /// THE REGRESSION. From the state the product actually starts in, a run must reach the Coder.
    /// </summary>
    [Fact]
    public async Task A_run_from_the_state_the_product_starts_in_reaches_the_coder()
    {
        var start = new RoutingState();
        var client = new Scripted(Said(SpecHandover), Wrote());
        var run = await new AgentLoop(client, Judge(start)).RunAsync("build me an EMA cross", "PACK", start);

        run.Turns[0].Role.Should().Be(AgentRole.Interviewer);
        run.Turns.Should().Contain(t => t.Role == AgentRole.Coder,
            "an interviewer that hands over a specification must not end the run");
        run.FinalState.HasSpec.Should().BeTrue("the handover is the only thing that ever sets it");
    }

    /// <summary>
    /// THE SECOND REGRESSION, and the one that cost a user hours of a working day.
    ///
    /// <para>The handover used to require the literal sentinel. A model that writes a perfectly good
    /// specification and simply does not repeat our magic sentence back — GLM 5.3 through TokenRouter,
    /// reported live — left <c>HasSpec</c> false, so the router chose the Interviewer again, which
    /// replied in prose again, which waited again. Answering changed nothing, because the answer was
    /// fed straight back into the same interview: hours of "the agent is waiting", and no code, ever.
    /// </para>
    ///
    /// <para>Absent the sentinel the reply is judged on what it IS. No questions block and no closing
    /// question mark means a specification, and the run proceeds to build it.</para>
    /// </summary>
    [Fact]
    public async Task A_specification_without_the_magic_sentence_still_reaches_the_coder()
    {
        const string spec =
            "Specification. Fade liquidity sweeps at the prior session low on 1-minute bars: enter when "
            + "a sweep through the level reverses within three bars on tape absorption, exit at VWAP, "
            + "stop below the sweep extreme. One position at a time. Needs L1 and the trade tape.";

        var start = new RoutingState();
        var client = new Scripted(Said(spec), Wrote());
        var run = await new AgentLoop(client, Judge(start)).RunAsync("fade the sweep", "PACK", start);

        run.Turns.Should().Contain(t => t.Role == AgentRole.Coder,
            "a specification is a specification whether or not the model echoed our sentinel");
        run.FinalState.HasSpec.Should().BeTrue();
    }

    /// <summary>
    /// The counterweight: prose that ENDS on a question is still a question, sentinel or no sentinel,
    /// so the run stops and lets the user answer it.
    /// </summary>
    [Theory]
    [InlineData("I can build that. Which instrument should it trade?")]
    [InlineData("Happy to write it. **Which timeframe do you want?**")]
    [InlineData("One thing first — is this for futures or spot?")]
    public async Task Prose_that_ends_on_a_question_still_waits(string asked)
    {
        var start = new RoutingState();
        var run = await new AgentLoop(new Scripted(Said(asked), Wrote()), Judge(start))
            .RunAsync("build something", "PACK", start);

        run.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);
        run.Turns.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interviewer);
    }

    /// <summary>
    /// A specification is allowed to RESTATE the questions it already settled, so the question test
    /// looks only at what the reply ends on. Scanning the whole body would find a question mark in
    /// almost every spec worth having and hand over never — the old bug with a new cause.
    /// </summary>
    [Fact]
    public async Task A_specification_that_recaps_its_questions_is_not_mistaken_for_asking_them()
    {
        const string spec = """
            Resolved: Which instrument? — ES. Which timeframe? — 5-minute bars.

            Entry on a close above the 20-bar high with volume at least 1.5x average; exit on an
            ATR(14) trailing stop. One position at a time, flat into the close.
            """;

        var start = new RoutingState();
        var run = await new AgentLoop(new Scripted(Said(spec), Wrote()), Judge(start))
            .RunAsync("momentum breakout", "PACK", start);

        run.Turns.Should().Contain(t => t.Role == AgentRole.Coder);
    }

    /// <summary>The other half: a real question still stops, because the answer is the user's.</summary>
    [Fact]
    public async Task An_interviewer_that_asks_something_still_waits_for_the_user()
    {
        var start = new RoutingState();
        var client = new Scripted(Said(SpecWithQuestion), Wrote());
        var run = await new AgentLoop(client, Judge(start)).RunAsync("build me something", "PACK", start);

        run.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);
        run.Turns.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interviewer);
        run.FinalState.HasSpec.Should().BeFalse("nothing was settled, so the gate stays shut");
    }

    /// <summary>
    /// A judged verdict must not un-know the specification. The judge advances a private copy seeded
    /// before the interview, so adopting it wholesale sent the next turn back to the Interviewer with
    /// working code already in hand.
    /// </summary>
    [Fact]
    public async Task A_compile_verdict_does_not_send_the_run_back_to_the_interviewer()
    {
        var start = new RoutingState();

        // A judge that reports on code while insisting the brief was never specified — which is exactly
        // what AuthoringJudge does, because its copy of the state predates the interview.
        static VerdictAndState Forgetful(IReadOnlyList<StrategyFile> _) => new(
            new VerificationReport([VerificationStep.Pass(VerificationRung.Compile)]),
            new RoutingState(HasCode: true, Compiles: true));

        var client = new Scripted(Said(SpecHandover), Wrote());
        var run = await new AgentLoop(client, Forgetful).RunAsync("brief", "PACK", start, maxTurns: 4);

        run.FinalState.HasSpec.Should().BeTrue();
        run.Turns.Skip(1).Should().NotContain(t => t.Role == AgentRole.Interviewer,
            "the brief was specified once and a compile cannot unspecify it");
    }

    /// <summary>A resumed run keeps the specification the last one paid for.</summary>
    [Fact]
    public async Task A_resumed_run_keeps_the_specification_and_the_original_brief()
    {
        var start = new RoutingState();
        var first = new Scripted(Said(SpecWithQuestion));
        var one = await new AgentLoop(first, Judge(start)).RunAsync(
            "an order book window", "PACK", start);

        one.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);

        var second = new Scripted(Said(SpecHandover), Wrote());
        var two = await new AgentLoop(second, Judge(one.FinalState)).RunAsync(
            "approved, now start building", "PACK", one.FinalState, resume: one.Context);

        // The Interviewer's second turn must still be able to see what it is building.
        second.Seen[0].Messages[0].Content.Should().Contain("an order book window");
        second.Seen[0].Messages[0].Content.Should().Contain("approved, now start building");
        two.Turns.Should().Contain(t => t.Role == AgentRole.Coder);
    }

    /// <summary>
    /// A question asked in bare prose — no block, no marker — waits. The safe direction: one more user
    /// turn costs a message, building the wrong unit costs the whole run.
    /// </summary>
    [Fact]
    public async Task A_question_asked_in_bare_prose_still_waits()
    {
        var start = new RoutingState();
        var client = new Scripted(Said("Which instrument, and over what timeframe?"), Wrote());
        var run = await new AgentLoop(client, Judge(start)).RunAsync("make me money", "PACK", start);

        run.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);
        run.FinalState.HasSpec.Should().BeFalse();
    }

    /// <summary>The reviewer never asks, so its report always hands over and the run can finish.</summary>
    [Fact]
    public async Task A_reviewer_hands_over_without_a_marker()
    {
        var start = new RoutingState(HasSpec: true, HasCode: true, Compiles: true);
        var client = new Scripted(Said("I read it for look-ahead bias and found none."));
        var run = await new AgentLoop(client, Judge(start)).RunAsync("brief", "PACK", start);

        run.Turns[0].Role.Should().Be(AgentRole.Reviewer);
        run.Outcome.Should().Be(AgentRunOutcome.Delivered);
        run.FinalState.Reviewed.Should().BeTrue();
    }

    /// <summary>
    /// Every canned "build it" reply is recognised as ending the interview. The view-model advances
    /// HasSpec from this, so pressing the escape does not depend on the model choosing to honour it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_canned_build_replies_end_the_interview(int index)
    {
        AuthoringAction.EndsTheInterview(AuthoringAction.Default[index].Reply).Should().BeTrue();
        AuthoringAction.EndsTheInterview(AuthoringAction.JustBuildIt).Should().BeTrue();
        AuthoringAction.EndsTheInterview("what about the stop?").Should().BeFalse();
        AuthoringAction.EndsTheInterview(null).Should().BeFalse();
    }

    /// <summary>
    /// The exact live failure: an Interviewer's specification carried a fenced LAYOUT SKETCH, and the
    /// extractor turned it into Strategy.cs.
    ///
    /// <para>119 characters of pseudo-code went to the compiler as a strategy. CS1003 and CS0103
    /// against a file nobody wrote, the Interviewer scored 0.00, and routing sent the next turn to a
    /// Fixer — which, because AgentContext kept only the Spec for that role, arrived with a diagnostic
    /// list and no code and could only decline. Three defects, one turn.</para>
    /// </summary>
    [Fact]
    public async Task A_specification_containing_a_fenced_sketch_is_not_compiled_as_a_unit()
    {
        var spec = """
            ## Specification — Triangle Area Cross

            The window is three panels:

            ```
            Rows(
              Columns( Panel "Price"  Star(3),
                       Panel "Signal" Pixels(260) ),
              Panel "Trade history" Pixels(150) )
            ```

            """ + AgentPrompts.Handover;

        var judged = 0;
        VerdictAndState Judge(IReadOnlyList<StrategyFile> files)
        {
            judged++;
            return new VerdictAndState(
                new VerificationReport([VerificationStep.Pass(VerificationRung.Compile)]),
                new RoutingState(HasSpec: true, HasCode: true, Compiles: true, Reviewed: true));
        }

        var client = new Scripted(Said(spec), Wrote());
        var run = await new AgentLoop(client, Judge).RunAsync("build me the triangles", "PACK", new RoutingState());

        // The sketch never reached the compiler, and the Interviewer's turn is not a code turn.
        run.Turns[0].Role.Should().Be(AgentRole.Interviewer);
        run.Turns[0].Files.Should().BeEmpty("a role that writes no code cannot contribute a file");

        // It handed over instead, and the Coder wrote the unit.
        run.Turns.Should().Contain(t => t.Role == AgentRole.Coder);
        judged.Should().Be(1, "only the Coder's file should ever have been judged");
    }

    /// <summary>Prose in a fence is refused whoever wrote it — the guard StrategyBuildSession has had
    /// since it cost three generations, now on this path too.</summary>
    [Fact]
    public async Task Prose_in_a_fence_from_a_coder_is_not_compiled_either()
    {
        var sketch = """
            Here is the layout.

            ```csharp
            // file: Strategy.cs
            Rows( Columns( Panel "Price" Star(3) ) )
            ```
            """;

        var judged = 0;
        VerdictAndState Judge(IReadOnlyList<StrategyFile> files)
        {
            judged++;
            return new VerdictAndState(new VerificationReport([]), new RoutingState(HasSpec: true));
        }

        var client = new Scripted(Said(sketch));
        var run = await new AgentLoop(client, Judge)
            .RunAsync("brief", "PACK", new RoutingState(HasSpec: true), maxTurns: 2);

        judged.Should().Be(0, "a fenced sketch is not a unit and must never reach the compiler");
        run.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);
    }

    /// <summary>
    /// A Quant may write the computation, and it must survive. The role arms used to match before the
    /// files arm, so the Quant's prose was kept and its code was dropped on the floor.
    /// </summary>
    [Fact]
    public void A_quant_that_writes_a_computation_keeps_both_halves()
    {
        var files = new[] { new StrategyFile("Maths.cs", "public static class M { }") };

        var context = new AgentContext("brief").With(AgentRole.Quant, "the derivation", files);

        context.Maths.Should().Be("the derivation");
        context.Files.Should().BeEquivalentTo(files, "WritesCode(Quant) is true, so its file is real");
    }

    /// <summary>The same edge from the Interviewer's side: keeping the spec must not drop the code.</summary>
    [Fact]
    public void An_interviewer_turn_does_not_erase_code_already_written()
    {
        var written = new[] { new StrategyFile("Unit.cs", "public sealed class U { }") };

        var context = new AgentContext("brief")
            .With(AgentRole.Coder, "here it is", written)
            .With(AgentRole.Interviewer, "and here is the spec", []);

        context.Spec.Should().Be("and here is the spec");
        context.Files.Should().BeEquivalentTo(written, "a later interview does not delete the unit");
    }

    /// <summary>
    /// A repair role with nothing to repair is a harness bug, not a model turn. Routing reads the
    /// ladder's verdict, which cannot see whether the context still holds the code.
    /// </summary>
    [Fact]
    public async Task A_fixer_with_no_code_becomes_a_coder_instead_of_a_wasted_turn()
    {
        // The state a dropped-file compile failure leaves behind: it failed, and there is no code.
        var start = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.Compile);

        var client = new Scripted(Wrote());
        var run = await new AgentLoop(client, Judge(start)).RunAsync("brief", "PACK", start, maxTurns: 1);

        run.Turns[0].Role.Should().Be(
            AgentRole.Coder, "there is nothing to fix, so the turn must write rather than decline");
    }

    /// <summary>
    /// Repair that repairs nothing must stop, not spend the budget.
    ///
    /// <para>From a user's trajectory log: 158 turns, 140 of them Fixer, every one scoring 0.00 against
    /// the same rung — 120 inside three minutes, sixty in a single minute, each re-sending a system
    /// prompt of about a hundred thousand characters. "It burnt all my session tokens in a few minutes."
    /// The turn budget is not a defence: sixteen turns of no progress costs sixteen turns.</para>
    /// </summary>
    [Fact]
    public async Task A_repair_loop_that_gains_no_ground_stops_instead_of_spending_the_budget()
    {
        // A judge that always returns the same verdict: compiled nothing, same rung, same findings.
        static VerdictAndState Stuck(IReadOnlyList<StrategyFile> _) => new(
            new VerificationReport(
            [
                VerificationStep.Fail(
                    VerificationRung.Compile, new VerificationFinding("CS0103", "does not exist", "fix it")),
            ]),
            new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.Compile));

        var client = new Scripted(Wrote());
        var run = await new AgentLoop(client, Stuck)
            .RunAsync("brief", "PACK", new RoutingState(HasSpec: true), maxTurns: 16);

        run.Outcome.Should().Be(AgentRunOutcome.Stalled);
        run.Turns.Count.Should().BeLessThan(
            16, "the run must stop at the wall rather than pay out the whole budget");
        run.Turns.Count.Should().BeLessThanOrEqualTo(AgentLoop.StallLimit + 1);
    }

    /// <summary>
    /// A repair that fixes some of the errors is progress and keeps its budget. Without this the stall
    /// guard would abandon a run that was converging, which is the expensive direction.
    /// </summary>
    [Fact]
    public async Task A_repair_that_removes_errors_is_not_a_stall()
    {
        var findings = 4;

        VerdictAndState Improving(IReadOnlyList<StrategyFile> _)
        {
            // One fewer error each turn — slow, but real progress.
            findings = Math.Max(0, findings - 1);
            var steps = findings > 0
                ? new[]
                {
                    VerificationStep.Fail(
                        VerificationRung.Compile,
                        [.. Enumerable.Range(0, findings)
                            .Select(i => new VerificationFinding($"CS{i}", "error", "fix"))]),
                }
                : [VerificationStep.Pass(VerificationRung.Compile)];

            return new VerdictAndState(
                new VerificationReport(steps),
                new RoutingState(
                    HasSpec: true, HasCode: true, Compiles: findings == 0, Reviewed: findings == 0,
                    FailedAt: findings > 0 ? VerificationRung.Compile : null));
        }

        var run = await new AgentLoop(new Scripted(Wrote()), Improving)
            .RunAsync("brief", "PACK", new RoutingState(HasSpec: true), maxTurns: 16);

        run.Outcome.Should().NotBe(
            AgentRunOutcome.Stalled, "each turn removed an error, which is exactly what repair is");
    }

    private sealed class Scripted(params StrategyCodegenResponse[] replies) : IStrategyCodegenClient
    {
        private int _index;

        public List<StrategyCodegenRequest> Seen { get; } = [];

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Seen.Add(request);
            return Task.FromResult(replies[Math.Min(_index++, replies.Length - 1)]);
        }
    }
}
