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
    public async Task<AgentRun> RunAsync(
        string brief,
        string sharedContext,
        RoutingState state,
        int maxTurns = 12,
        IProgress<AgentTurn>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        if (maxTurns <= 0) throw new ArgumentOutOfRangeException(nameof(maxTurns));

        // Artifacts, not a transcript. Every turn sends the CURRENT state of the work rather than the
        // history of how it got there, so context is bounded by the size of the unit instead of growing
        // with the number of turns — and each agent reads only what its job acts on.
        var context = new AgentContext(brief);
        var turns = new List<AgentTurn>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            var decision = AgentRouter.Choose(state, Reliability);
            if (decision is null)
                return new AgentRun(AgentRunOutcome.Delivered, state, turns, Context: context);

            var response = await _client.GenerateAsync(
                new StrategyCodegenRequest(
                    sharedContext,
                    [new CodegenMessage(CodegenRole.User, context.ComposeFor(decision.Role))],
                    AgentPrompts.For(decision.Role)),
                ct).ConfigureAwait(false);

            if (response.Error is { Length: > 0 } error)
                return new AgentRun(AgentRunOutcome.ProviderFailed, state, turns, error, context);

            // A role that does not write code answering without code is doing its job — and a role that
            // does write code answering with none is asking a question, which is also a turn rather than
            // a failure. Either way the loop stops and waits for the user.
            if (response.FileList.Count == 0)
            {
                // An Interviewer's spec and a Quant's derivation ARE their output, so they are kept even
                // though no file came back; a question from a coding role leaves the context unchanged.
                context = context.With(decision.Role, response.RawText ?? string.Empty, []);
                var asked = new AgentTurn(decision.Role, decision.Weights, response.RawText ?? string.Empty, [], Reward: 0d);
                turns.Add(asked);
                progress?.Report(asked);
                return new AgentRun(AgentRunOutcome.AwaitingUser, state, turns, Context: context);
            }

            var verdict = _judge(response.FileList);
            var reward = LadderFeedback.RewardFor(verdict.Report);
            Reliability.Record(decision.Role, reward);

            // The judge owns the facts a report cannot carry — whether this unit owes a picture, whether
            // a human has reviewed it — so it returns the next state rather than the loop inferring one.
            state = verdict.State;
            context = context.With(decision.Role, response.RawText ?? string.Empty, response.FileList)
                             .With(verdict.Report);
            var completed = new AgentTurn(
                decision.Role, decision.Weights, response.RawText ?? string.Empty, response.FileList, reward);
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
        }

        return new AgentRun(AgentRunOutcome.BudgetExhausted, state, turns, Context: context);
    }
}

/// <summary>What the ladder concluded, and where that leaves the session.</summary>
public sealed record VerdictAndState(
    Verification.VerificationReport Report,
    RoutingState State);
