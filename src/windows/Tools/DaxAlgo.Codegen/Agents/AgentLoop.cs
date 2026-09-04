using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>One turn of the loop, kept so the whole run can be read back afterwards.</summary>
/// <param name="Role">Who took it.</param>
/// <param name="Weights">The posterior the router used — the reasoning, not just the outcome.</param>
/// <param name="Reply">What the model said.</param>
/// <param name="Files">Any code it emitted. Empty is normal for the roles that do not write code.</param>
/// <param name="Reward">What the turn earned, once the ladder had judged it.</param>
public sealed record AgentTurn(
    AgentRole Role,
    IReadOnlyDictionary<AgentRole, double> Weights,
    string Reply,
    IReadOnlyList<StrategyFile> Files,
    double Reward);

/// <summary>How a run ended.</summary>
public enum AgentRunOutcome
{
    /// <summary>The unit cleared the ladder and was reviewed.</summary>
    Delivered,

    /// <summary>The turn budget ran out first.</summary>
    BudgetExhausted,

    /// <summary>
    /// Repair stopped repairing: several turns in a row bought no ground on the ladder, so the run
    /// ended rather than spending the rest of the budget on the same wall.
    /// </summary>
    Stalled,

    /// <summary>An agent asked the user something. Not a failure — the loop is waiting.</summary>
    AwaitingUser,

    /// <summary>The provider failed: auth, timeout, no CLI.</summary>
    ProviderFailed,
}

/// <summary>Everything a run produced.</summary>
public sealed record AgentRun(
    AgentRunOutcome Outcome,
    RoutingState FinalState,
    IReadOnlyList<AgentTurn> Turns,
    string? Error = null,
    AgentContext? Context = null);

/// <summary>
/// Drives the agents: route, ask, judge, record, repeat.
///
/// <para>Deliberately thin, and deliberately not wired to the authoring pane yet. Whether a user sees
/// each agent's turn, approves between them, or only meets the finished unit is a product decision
/// about their time and their tokens — not one this class should make by being the only thing that
/// exists.</para>
///
/// <para>Everything it needs is injected, so a whole run can be driven from a fake client and asserted
/// on. A loop that can only be observed against a live provider is a loop nobody will change.</para>
/// </summary>
public sealed class AgentLoop(
    IStrategyCodegenClient client,
    Func<IReadOnlyList<StrategyFile>, VerdictAndState> judge,
    AgentReliability? reliability = null,
    TrajectoryLog? trajectory = null)
{
    private readonly IStrategyCodegenClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly Func<IReadOnlyList<StrategyFile>, VerdictAndState> _judge =
        judge ?? throw new ArgumentNullException(nameof(judge));

    /// <summary>The estimate, shared across runs so it accumulates. Exposed so a caller can persist it.</summary>
    public AgentReliability Reliability { get; } = reliability ?? new AgentReliability();

    /// <summary>Where each turn's numbers go, or null to record nothing.
    ///
    /// <para>Optional because a run must not fail for want of a log, and null in the tests that are about
    /// routing rather than accounting. Non-null in the application, because the split into six agents was
    /// justified on cost and you cannot minimise what you do not measure.</para></summary>
    public TrajectoryLog? Trajectory { get; } = trajectory;

    /// <summary>
    /// Runs until the unit is delivered, an agent asks a question, or the budget is spent.
    /// </summary>
    /// <param name="brief">What the user asked for.</param>
    /// <param name="sharedContext">The generated surface plus conventions — the cached prefix.</param>
    /// <param name="state">Where the session starts. A fresh brief starts at its defaults.</param>
    /// <param name="maxTurns">
    /// The budget, and it is the user's money. A loop that can spend without limit will, and the honest
    /// end to a brief that cannot be satisfied is "here is what I built and what did not work" rather
    /// than another attempt.
    /// </param>
    /// <param name="progress">
    /// Reports each turn as it completes. A run of ten turns takes minutes, and a pane that shows
    /// nothing until the end reads as a hang — the agents being visible is what makes the wait legible
    /// and what justifies its cost to the person paying for it.
    /// </param>
    /// <param name="resume">
    /// The context a previous run left behind, so a second user turn continues the same piece of work
    /// instead of starting a new one. Null begins from the brief alone.
    /// </param>
    public async Task<AgentRun> RunAsync(
        string brief,
        string sharedContext,
        RoutingState state,
        int maxTurns = 12,
        IProgress<AgentTurn>? progress = null,
        CancellationToken ct = default,
        AgentContext? resume = null,
        IProgress<AgentRole>? starting = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        if (maxTurns <= 0) throw new ArgumentOutOfRangeException(nameof(maxTurns));

        // Artifacts, not a transcript. Every turn sends the CURRENT state of the work rather than the
        // history of how it got there, so context is bounded by the size of the unit instead of growing
        // with the number of turns — and each agent reads only what its job acts on.
        // A resumed run keeps the spec, the maths and the code it already has, and folds this turn's
        // message into the brief. Rebuilding from `brief` alone is what made every interview restart:
        // turn two was handed the user's answer with nothing it was an answer TO.
        var context = resume is null ? new AgentContext(brief) : resume.WithUserReply(brief);

        // How far up the ladder anything has got, and how many turns since that last improved.
        //
        // THE EXPENSIVE FAILURE MODE, and it is invisible without this. Measured from a user machine's
        // trajectory log: 158 turns, 140 of them Fixer, every one scoring 0.00 against the same rung --
        // and 120 of them inside three minutes, sixty in one. Each re-sends a system prompt of roughly a
        // hundred thousand characters, so a loop that cannot converge spends a token budget at the speed
        // of the provider rather than the speed of progress. The turn budget is no defence: sixteen
        // turns of no progress costs sixteen turns.
        //
        // The bandit cannot help either. A compile failure makes the prior Only(Fixer), and reliability
        // may only redistribute weight among eligible agents -- with one eligible agent a score of zero
        // changes nothing, so the Fixer that has failed a hundred times is still the only choice.
        // int.MinValue, not -1: a verdict that clears no rung has NEGATIVE height (it is scored down by
        // its findings), so seeding at -1 made genuine progress -- four errors becoming three -- read as
        // no progress and stalled a converging run at turn three. Caught by the test that asserts the
        // opposite direction, which is the one worth writing.
        var best = int.MinValue;
        var stalled = 0;
        var turns = new List<AgentTurn>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            var decision = AgentRouter.Choose(state, Reliability);
            if (decision is null)
                return new AgentRun(AgentRunOutcome.Delivered, state, turns, Context: context);

            // A repair role needs something to repair. Routing works from the ladder's verdict, which
            // cannot see whether the context still holds the code, so a compile failure on files that
            // were dropped upstream sent a Fixer a diagnostic list and nothing else — a turn it can only
            // decline, billed in full. Observed live: "I do not have the source file ... fixing it would
            // mean inventing the unit rather than repairing it", which was the right answer to a
            // question we should never have asked.
            if (context.Files is not { Count: > 0 } && decision.Role is AgentRole.Fixer or AgentRole.Painter)
                decision = new RoutingDecision(AgentRole.Coder, decision.Weights);

            // Reported BEFORE the call, because the call is the part that takes minutes. `progress`
            // fires when a turn is finished, which is the wrong end for a live indicator: it told the
            // user what had already happened and nothing about the silence they were sitting in.
            starting?.Report(decision.Role);

            var response = await _client.GenerateAsync(
                new StrategyCodegenRequest(
                    sharedContext,
                    [new CodegenMessage(CodegenRole.User, context.ComposeFor(decision.Role))],
                    AgentPrompts.For(decision.Role)),
                ct).ConfigureAwait(false);

            if (response.Error is { Length: > 0 } error)
                return new AgentRun(AgentRunOutcome.ProviderFailed, state, turns, error, context);

            // What this turn may contribute as CODE, which is not the same as what came back fenced.
            //
            // Two filters, both learned from one live run. A role that does not write code contributes
            // none: an Interviewer wrote a specification containing a fenced LAYOUT SKETCH, the
            // extractor made it Strategy.cs, and 119 characters of pseudo-code went to the compiler as
            // a strategy — CS1003 and CS0103 against a file nobody wrote, the Interviewer scored 0.00,
            // and the next turn was a repair turn with nothing to repair. Its own prompt already says
            // "write no code"; this is that instruction enforced rather than requested.
            //
            // And prose in a fence is not code whoever wrote it. StrategyBuildSession has refused it
            // since the day it cost three generations — the fix loop reads CS1003 and tries to FIX THE
            // PROSE — but the guard lived on that path only, and this one compiled it happily. Same
            // shape as every other defect in this area: two paths to one state, one of them finished.
            var files = AgentPrompts.WritesCode(decision.Role)
                ? response.FileList.Where(f => CodegenCodeExtractor.LooksLikeCode(f.Content)).ToArray()
                : [];

            // A role that does not write code answering without code is doing its job — and a role that
            // does write code answering with none is asking a question, which is also a turn rather than
            // a failure. Either way the loop stops and waits for the user.
            if (files.Length == 0)
            {
                // An Interviewer's spec and a Quant's derivation ARE their output, so they are kept even
                // though no file came back; a question from a coding role leaves the context unchanged.
                var reply = response.RawText ?? string.Empty;
                context = context.With(decision.Role, reply, []);
                var spoke = new AgentTurn(decision.Role, decision.Weights, reply, [], Reward: 0d);
                turns.Add(spoke);
                progress?.Report(spoke);

                // THE HANDOVER. A role that writes no code has finished its turn, not failed it, and the
                // run must go on to the role that acts on what it produced. Without this the loop stopped
                // on every Interviewer reply and nothing in the product ever set HasSpec, so the prior
                // returned Only(Interviewer) forever: six user turns, six interviews, no code. The
                // Interviewer's own prompt promises the opposite -- "If the user says to build it, the
                // interview is over. Hand over immediately" -- and there was no code to perform it.
                //
                // A question is the one reply that must still stop, because the answer is the user's.
                if (HandsOver(decision.Role, reply))
                {
                    state = HandOver(state, decision.Role);
                    continue;
                }

                return new AgentRun(AgentRunOutcome.AwaitingUser, state, turns, Context: context);
            }

            var verdict = _judge(files);
            var reward = LadderFeedback.RewardFor(verdict.Report);
            Reliability.Record(decision.Role, reward);

            // Ground gained, counted as rungs cleared and then as findings removed at the same height.
            // A repair that fixes one of four errors IS progress and keeps its budget; one that returns
            // the same verdict is not, whatever it says about itself.
            var height = verdict.Report.RungsCleared * 1000 - verdict.Report.Findings.Count;
            if (height > best) { best = height; stalled = 0; } else { stalled++; }

            // The judge owns the facts a report cannot carry — whether this unit owes a picture, whether
            // a human has reviewed it — so it returns the next state rather than the loop inferring one.
            // The judge owns what the ladder can see; the conversation owns the rest. AuthoringJudge
            // advances a PRIVATE copy of the state that was seeded before the interview happened, so
            // adopting the verdict wholesale threw HasSpec away and routed the next turn straight back
            // to the Interviewer -- with working code already in hand. LadderFeedback.Advance documents
            // exactly this ("would ... send the loop back to the Interviewer"); it carries the fact
            // through faithfully, from a copy that never learned it.
            state = verdict.State with { HasSpec = state.HasSpec, NeedsMaths = state.NeedsMaths };
            context = context.With(decision.Role, response.RawText ?? string.Empty, files)
                             .With(verdict.Report);
            var completed = new AgentTurn(
                decision.Role, decision.Weights, response.RawText ?? string.Empty, files, reward);
            turns.Add(completed);
            progress?.Report(completed);

            // Numbers and finding codes only — never the brief, the reply or the code. The log records
            // what a turn cost and what it bought, which is the evidence for whether six agents beat one
            // conversation. A logging fault is not worth a run: the user came for a strategy.
            try
            {
                Trajectory?.Append(completed, verdict.Report, response.Usage);
            }
            catch (Exception)
            {
                // Deliberately swallowed. TrajectoryLog already tolerates a malformed line on read.
            }

            // Three turns that bought nothing is a wall, not a bad run of luck. Stopping here is the
            // honest end this class already describes: "here is what I built and what did not work"
            // rather than another attempt the user pays for.
            if (stalled >= StallLimit)
                return new AgentRun(AgentRunOutcome.Stalled, state, turns, Context: context);
        }

        return new AgentRun(AgentRunOutcome.BudgetExhausted, state, turns, Context: context);
    }

    /// <summary>How many consecutive turns may buy no ground before the run stops.</summary>
    public const int StallLimit = 3;

    /// <summary>
    /// What a role that writes no code has established, once it hands over.
    ///
    /// <para>This is the only place in the product that ever sets <see cref="RoutingState.HasSpec"/>.
    /// It was missing outright: the flag was read by <see cref="RoutingPrior"/>, defaulted to false by
    /// the record, and set to true in twenty-eight places, every one of them a test. So every test
    /// began past the gate the application could never get through, and the one production caller --
    /// <c>new RoutingState()</c> in the authoring view-model -- routed to the Interviewer on every turn
    /// for the life of the session.</para>
    ///
    /// <para>Each role clears the condition that made it eligible, which is what stops the prior from
    /// choosing it again on the next pass. A role that cleared nothing would loop.</para>
    /// </summary>
    /// <summary>
    /// Whether a reply that carried no code has finished the role's job or is still asking.
    ///
    /// <para>A Reviewer never asks — it reports on finished code and its report IS the review, so it
    /// always hands over. An Interviewer legitimately does both, and it is told apart by what it wrote:
    /// a <c>questions</c> block or a closing question means it is waiting; anything else is a finished
    /// specification.</para>
    /// </summary>
    private static bool HandsOver(AgentRole role, string reply)
    {
        if (AgentPrompts.WritesCode(role)) return false;
        if (role == AgentRole.Reviewer) return true;
        if (AuthoringQuestions.Parse(reply).Count > 0) return false;
        if (reply.Contains(AgentPrompts.Handover, StringComparison.OrdinalIgnoreCase)) return true;

        // NEITHER SIGNAL PRESENT, AND THIS IS THE LINE THE FEATURE DIED ON.
        //
        // The rule was "no sentinel ⇒ wait", justified as the safe direction: one wasted user turn
        // rather than building the wrong unit. It is not one turn. Waiting leaves HasSpec false, so the
        // router picks the Interviewer again, which replies in prose again, which waits again — and
        // ANSWERING IT CHANGES NOTHING, because the answer is fed straight back into the same
        // interview. The user sees "the agent is waiting" forever and no code is ever written.
        // Reported from a real session: hours of it, on GLM 5.3 through TokenRouter — a model that
        // writes a perfectly good specification and simply does not repeat our magic sentence back.
        //
        // So the sentinel is a fast path now, not the only path. Without it the reply is judged on what
        // it IS. An interviewer that wants something has two ways to say so — a questions block, or a
        // question mark — and a reply using neither is not waiting on anybody: it is the specification,
        // and the run should go and build it.
        //
        // The default is inverted deliberately. The old one made a model's failure to emit one exact
        // string cost the whole feature; this one costs, at worst, a build from a spec the user
        // corrects on the next turn — which is the loop working rather than the loop stuck.
        return !EndsWithQuestion(reply);
    }

    /// <summary>
    /// Whether a reply's closing words are asking the user something.
    ///
    /// <para>Only the TAIL is examined. A good specification restates the questions it already resolved
    /// ("Which timeframe? — 5-minute bars"), so scanning the whole reply for a question mark would find
    /// one in nearly every spec worth having and hand over never. What a turn ends on is what it wants
    /// next.</para>
    /// </summary>
    internal static bool EndsWithQuestion(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return false;

        var trimmed = reply.AsSpan().TrimEnd();

        // Markdown emphasis and closing punctuation routinely follow the mark, and a reply ending
        // "**...which instrument?**" is every bit as much a question as one ending in a bare "?".
        while (trimmed.Length > 0 && trimmed[^1] is '*' or '_' or '`' or '"' or '\'' or ')' or ']')
            trimmed = trimmed[..^1].TrimEnd();

        return trimmed.Length > 0 && trimmed[^1] == '?';
    }

    private static RoutingState HandOver(RoutingState state, AgentRole role) => role switch
    {
        AgentRole.Interviewer => state with { HasSpec = true },
        AgentRole.Quant => state with { NeedsMaths = false },

        // The reviewer writes prose about finished code, and that prose IS the review. The ladder must
        // never set this -- a rung cannot read for look-ahead bias -- but a Reviewer turn plainly can,
        // and a later code turn resets it through the judge, which is right: changed code is unreviewed.
        AgentRole.Reviewer => state with { Reviewed = true },
        _ => state,
    };
}

/// <summary>What the ladder concluded, and where that leaves the session.</summary>
public sealed record VerdictAndState(
    Verification.VerificationReport Report,
    RoutingState State);
