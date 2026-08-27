using TradingTerminal.Core.Execution;
using TradingTerminal.Execution.Oms;
using Xunit;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The application-wide Paper/Real switch, as an actual gate on live dispatch.
///
/// <para><b>This did not exist until 2026-08-27.</b> <see cref="ExecutionModeSelection"/> was defined
/// in Core, registered in DI and bound to the login window's toggle — and read by nothing on the
/// execution path. Typing LIVE armed a flag no order ever consulted, so real money was guarded by the
/// per-account confirmation alone, while the class doc and CLAUDE.md both stated that a live order
/// required both gates. Nothing failed; the outer gate simply wasn't there.</para>
///
/// <para>The tests below are written against the property that matters rather than the wiring: an
/// order bound for a LIVE broker endpoint is refused unless someone armed the session, a PAPER
/// endpoint is untouched either way, and <b>cancelling is never refused</b>.</para>
/// </summary>
public sealed class TradingModeGateTests
{
    /// <summary>The phrase the gate puts in its rejection. Asserted on directly because the other live
    /// guardrails — the lease and the reconciliation admission — can also block a live command, and a
    /// test that only checked "was blocked" would pass just as happily when this gate did nothing.</summary>
    private const string GateReason = "Real trading is not armed";

    private static OrderCommandContext Context(CanonicalOrderInstruction order, string suffix) =>
        new(
            OmsTestData.Causation($"{order.Identity.ClientOrderId.Value}-{suffix}"),
            OmsTestData.Dedup($"{order.Identity.ClientOrderId.Value}-{suffix}"));

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static ExecutionModeSelection Armed()
    {
        var mode = new ExecutionModeSelection();
        Assert.True(mode.TryEnableReal(
            ExecutionModeSelection.RequiredAcknowledgement, "test", out _));
        return mode;
    }

    /// <summary>
    /// The simulated adapter with one thing changed: it reports a LIVE endpoint.
    ///
    /// <para>Everything else delegates, so the only variable between these tests and the ordinary
    /// coordinator suite is the property the gate reads.</para>
    /// </summary>
    private sealed class LiveEndpointAdapter(IBrokerExecutionAdapter inner) : IBrokerExecutionAdapter
    {
        public ExecutionMode Mode => ExecutionMode.Live;

        public string BrokerId => inner.BrokerId;
        public BrokerExecutionAccount Account => inner.Account;
        public BrokerExecutionSession Session => inner.Session;
        public BrokerExecutionCapabilities Capabilities => inner.Capabilities;

        public event Action<BrokerAdapterEvent>? EventReceived
        {
            add => inner.EventReceived += value;
            remove => inner.EventReceived -= value;
        }

        public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command) => inner.Submit(command);
        public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command) => inner.Cancel(command);
        public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command) => inner.Replace(command);
        public BrokerOrderQueryResult Query(BrokerOrderQuery query) => inner.Query(query);

        public BrokerReconciliationSnapshot CaptureReconciliationSnapshot() =>
            inner.CaptureReconciliationSnapshot();
    }

    private sealed record Harness(
        ExecutionCoordinator Coordinator,
        OrderManagementService Oms,
        IBrokerExecutionAdapter Adapter,
        ControllableAdapterEventScheduler Scheduler) : IDisposable
    {
        public void Dispose() => Coordinator.Dispose();
    }

    private static Harness Build(bool live, ExecutionModeSelection? mode, out CanonicalOrderInstruction order)
    {
        var clock = Clock();
        order = OmsTestData.Instruction("trading-mode-gate");

        var venue = new DeterministicSimulatedVenue(
            clock,
            [new VenueSubmitPlan(order.Identity.ClientOrderId, VenueSubmitOutcome.Accepted, [])]);
        var scheduler = new ControllableAdapterEventScheduler();

        IBrokerExecutionAdapter adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
        if (live) adapter = new LiveEndpointAdapter(adapter);

        var oms = new OrderManagementService(
            new InMemoryOrderEventStore(), OmsTestData.RiskEngine(), venue, clock);

        return new Harness(
            new ExecutionCoordinator(oms, [adapter], workerCapacity: 64, tradingMode: mode),
            oms,
            adapter,
            scheduler);
    }

    /// <summary>Walks an order to Armed, which is the state release requires.</summary>
    private static void Arm(Harness harness, CanonicalOrderInstruction order)
    {
        var id = order.Identity.ClientOrderId;
        Assert.True(harness.Oms.CreateDraft(order, Context(order, "draft")).IsSuccess);
        Assert.True(harness.Coordinator.Validate(
            harness.Adapter.Account, id, OmsTestData.RiskSnapshot(),
            Context(order, "validate")).IsSuccess);
        Assert.True(harness.Oms.Prepare(id, Context(order, "prepare")).IsSuccess);
        Assert.True(harness.Coordinator.Arm(
            harness.Adapter.Account, id, Context(order, "arm")).IsSuccess);
    }

    // ── the gate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_live_order_is_refused_while_the_session_is_in_paper()
    {
        using var harness = Build(live: true, mode: new ExecutionModeSelection(), out var order);
        Arm(harness, order);

        var result = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account, order.Identity.ClientOrderId,
            Context(order, "release"));

        Assert.False(result.IsSuccess);
        Assert.Contains(GateReason, result.OmsResult.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_coordinator_given_no_switch_at_all_refuses_live_dispatch()
    {
        // The failure mode this guards: a composition that forgets to supply the switch. It must deny,
        // not run ungated — a missing safety gate cannot be allowed to mean "no safety gate".
        using var harness = Build(live: true, mode: null, out var order);
        Arm(harness, order);

        var result = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account, order.Identity.ClientOrderId,
            Context(order, "release"));

        Assert.False(result.IsSuccess);
        Assert.Contains(GateReason, result.OmsResult.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arming_the_session_gets_a_live_order_past_this_gate()
    {
        // It may still be stopped further along — a live dispatch also needs an execution lease and a
        // reconciliation admission — but it must not be stopped *here*, or arming would do nothing.
        using var harness = Build(live: true, mode: Armed(), out var order);
        Arm(harness, order);

        var result = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account, order.Identity.ClientOrderId,
            Context(order, "release"));

        Assert.DoesNotContain(GateReason, result.OmsResult.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_paper_endpoint_is_unaffected_by_the_switch()
    {
        // The switch guards real money, not network traffic. If it blocked a broker's paper endpoint
        // too, a user would have to arm REAL trading in order to paper-trade — which would be a worse
        // safety position than having no switch.
        using var harness = Build(live: false, mode: new ExecutionModeSelection(), out var order);
        Arm(harness, order);

        var result = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account, order.Identity.ClientOrderId,
            Context(order, "release"));

        Assert.True(result.IsSuccess);
    }

    // ── the thing the gate must never do ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_live_order_is_never_refused_by_the_switch()
    {
        // The dangerous case. Someone disarms while orders are working at a broker; if cancel were
        // gated too, those orders would be stranded live with no way to pull them, and the safety
        // switch would have become the hazard. Cancel may fail for other reasons — it must never fail
        // for this one.
        using var harness = Build(live: true, mode: new ExecutionModeSelection(), out var order);
        Arm(harness, order);

        var result = await harness.Coordinator.CancelAsync(
            harness.Adapter.Account, order.Identity.ClientOrderId,
            Context(order, "cancel"));

        Assert.DoesNotContain(GateReason, result.OmsResult.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    // ── the switch itself ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_switch_starts_disarmed()
    {
        var mode = new ExecutionModeSelection();

        Assert.True(mode.IsPaper);
        Assert.False(mode.IsReal);
        Assert.Equal(TradingMode.Paper, mode.Mode);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("Live")]
    [InlineData("LIVE ")]
    [InlineData("")]
    [InlineData(null)]
    public void Arming_needs_the_word_typed_exactly(string? attempt)
    {
        // Not a checkbox, not a case-insensitive match, not a trimmed one. The whole point is that it
        // cannot be reached by a stray click or a sloppy binding.
        var mode = new ExecutionModeSelection();

        Assert.False(mode.TryEnableReal(attempt, "test", out _));
        Assert.False(mode.IsReal);
    }

    [Fact]
    public void Disarming_is_never_refused()
    {
        var mode = Armed();

        mode.SetPaper();

        Assert.True(mode.IsPaper);
    }
}
