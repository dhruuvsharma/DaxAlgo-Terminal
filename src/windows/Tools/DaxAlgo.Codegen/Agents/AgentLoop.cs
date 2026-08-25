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
    string? Error = null);

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
    AgentReliability? reliability = null)
{
    private readonly IStrategyCodegenClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly Func<IReadOnlyList<StrategyFile>, VerdictAndState> _judge =
        judge ?? throw new ArgumentNullException(nameof(judge));

    /// <summary>The estimate, shared across runs so it accumulates. Exposed so a caller can persist it.</summary>
    public AgentReliability Reliability { get; } = reliability ?? new AgentReliability();

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
    public async Task<AgentRun> RunAsync(
        string brief,
        string sharedContext,
        RoutingState state,
        int maxTurns = 12,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        if (maxTurns <= 0) throw new ArgumentOutOfRangeException(nameof(maxTurns));

        var messages = new List<CodegenMessage> { new(CodegenRole.User, brief) };
        var turns = new List<AgentTurn>();

        for (var turn = 0; turn < maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            var decision = AgentRouter.Choose(state, Reliability);
            if (decision is null)
                return new AgentRun(AgentRunOutcome.Delivered, state, turns);

            var response = await _client.GenerateAsync(
                new StrategyCodegenRequest(sharedContext, messages, AgentPrompts.For(decision.Role)),
                ct).ConfigureAwait(false);

            if (response.Error is { Length: > 0 } error)
                return new AgentRun(AgentRunOutcome.ProviderFailed, state, turns, error);

            messages.Add(new CodegenMessage(CodegenRole.Assistant, response.RawText));

            // A role that does not write code answering without code is doing its job — and a role that
            // does write code answering with none is asking a question, which is also a turn rather than
            // a failure. Either way the loop stops and waits for the user.
            if (response.FileList.Count == 0)
            {
                turns.Add(new AgentTurn(decision.Role, decision.Weights, response.RawText, [], Reward: 0d));
                return new AgentRun(AgentRunOutcome.AwaitingUser, state, turns);
            }

            var verdict = _judge(response.FileList);
            var reward = LadderFeedback.RewardFor(verdict.Report);
            Reliability.Record(decision.Role, reward);

            // The judge owns the facts a report cannot carry — whether this unit owes a picture, whether
            // a human has reviewed it — so it returns the next state rather than the loop inferring one.
            state = verdict.State;
            turns.Add(new AgentTurn(decision.Role, decision.Weights, response.RawText, response.FileList, reward));
        }

        return new AgentRun(AgentRunOutcome.BudgetExhausted, state, turns);
    }
}

/// <summary>What the ladder concluded, and where that leaves the session.</summary>
public sealed record VerdictAndState(
    Verification.VerificationReport Report,
    RoutingState State);
