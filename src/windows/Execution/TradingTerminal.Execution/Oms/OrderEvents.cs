using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Execution.Oms;

/// <summary>Semantic fact recorded in the append-only OMS event ledger.</summary>
public enum OrderEventKind : byte
{
    /// <summary>The canonical instruction was captured as a draft.</summary>
    DraftCreated = 0,

    /// <summary>Versioned pre-trade risk accepted the complete order unchanged.</summary>
    RiskAccepted = 1,

    /// <summary>Versioned pre-trade risk rejected the complete order without clamping.</summary>
    RiskRejected = 2,

    /// <summary>Structure or venue capability validation rejected the order.</summary>
    ValidationRejected = 3,

    /// <summary>Invariant dispatch data was prepared.</summary>
    Prepared = 4,

    /// <summary>A fresh authorization armed the prepared order.</summary>
    Armed = 5,

    /// <summary>The send was durably marked as started.</summary>
    SendStarted = 6,

    /// <summary>The venue proved that send failed before acceptance, permitting safe retry.</summary>
    SendFailedBeforeAcceptance = 7,

    /// <summary>Local submission completed and acknowledgement is pending.</summary>
    SubmissionRecorded = 8,

    /// <summary>The simulated venue acknowledged the order.</summary>
    VenueAcknowledged = 9,

    /// <summary>An exact immutable fill was observed.</summary>
    FillReceived = 10,

    /// <summary>Cancellation was requested while the original order remained fillable.</summary>
    CancelRequested = 11,

    /// <summary>Cancellation was confirmed.</summary>
    CancelConfirmed = 12,

    /// <summary>Replacement was requested while the original order remained fillable.</summary>
    ReplaceRequested = 13,

    /// <summary>The simulated venue confirmed replacement terms.</summary>
    ReplaceConfirmed = 14,

    /// <summary>The simulated venue observably rejected the order.</summary>
    VenueRejected = 15,

    /// <summary>The simulated venue expired the order.</summary>
    Expired = 16,

    /// <summary>Send visibility could not be proved, so retry is blocked.</summary>
    OutcomeUnknown = 17,

    /// <summary>Explicit reconciliation began for an unknown outcome.</summary>
    ReconciliationStarted = 18,

    /// <summary>Explicit evidence closed a reconciliation case.</summary>
    Reconciled = 19,

    /// <summary>Non-transition recovery evidence was appended.</summary>
    RecoveryObserved = 20,

    /// <summary>Fresh versioned risk accepted changed replacement terms.</summary>
    ReplaceRiskAccepted = 21,

    /// <summary>Fresh versioned risk rejected changed replacement terms without changing the order.</summary>
    ReplaceRiskRejected = 22,

    /// <summary>A separate exact commission callback was observed as non-economic evidence.</summary>
    CommissionObserved = 23,

    /// <summary>An exact account-position callback was observed as reconciliation evidence.</summary>
    PositionObserved = 24,
}

/// <summary>Origin category for transactional inbox deduplication.</summary>
public enum OrderEventSource : byte
{
    /// <summary>An explicit OMS command.</summary>
    Command = 0,

    /// <summary>The existing versioned risk engine.</summary>
    Risk = 1,

    /// <summary>The deterministic in-memory simulated venue.</summary>
    SimulatedVenue = 2,

    /// <summary>Crash recovery logic.</summary>
    Recovery = 3,

    /// <summary>Explicit reconciliation evidence.</summary>
    Reconciliation = 4,
}

/// <summary>
/// Proposed immutable fact before the persistence seam assigns aggregate sequence and hash-chain
/// fields. All times and causation are explicit; the store never consults a wall clock.
/// </summary>
public sealed record OrderEventDraft(
    ClientOrderId AggregateId,
    OrderEventKind Kind,
    OrderLifecycleState StateAfter,
    OrderEventSource Source,
    DeduplicationKey DeduplicationKey,
    DateTime OccurredAtUtc,
    CausationId CausationId,
    CanonicalOrderInstruction? Instruction = null,
    RiskDecisionRecord? RiskDecision = null,
    BrokerOrderId? BrokerOrderId = null,
    ExchangeOrderId? ExchangeOrderId = null,
    FillExecution? Fill = null,
    CanonicalOrderTerms? ReplacementTerms = null,
    ReconciliationResolution? Reconciliation = null,
    string? Reason = null);

/// <summary>
/// Immutable append-only order fact from roadmap sections 13.1 and 13.4. Sequence and previous hash
/// are scoped to <see cref="AggregateId"/>; <see cref="EventHash"/> covers every recorded field.
/// </summary>
public sealed record OrderEvent(
    ClientOrderId AggregateId,
    long AggregateSequence,
    OrderEventKind Kind,
    OrderLifecycleState? StateBefore,
    OrderLifecycleState StateAfter,
    OrderEventSource Source,
    DeduplicationKey DeduplicationKey,
    DateTime OccurredAtUtc,
    DateTime RecordedAtUtc,
    CausationId CausationId,
    string PreviousEventHash,
    string EventHash,
    CanonicalOrderInstruction? Instruction = null,
    RiskDecisionRecord? RiskDecision = null,
    BrokerOrderId? BrokerOrderId = null,
    ExchangeOrderId? ExchangeOrderId = null,
    FillExecution? Fill = null,
    CanonicalOrderTerms? ReplacementTerms = null,
    ReconciliationResolution? Reconciliation = null,
    string? Reason = null);

internal static class OrderEventHash
{
    internal const string EmptyPreviousHash = "";

    internal static string Compute(OrderEvent orderEvent) =>
        Compute(
            orderEvent.AggregateId,
            orderEvent.AggregateSequence,
            orderEvent.Kind,
            orderEvent.StateBefore,
            orderEvent.StateAfter,
            orderEvent.Source,
            orderEvent.DeduplicationKey,
            orderEvent.OccurredAtUtc,
            orderEvent.RecordedAtUtc,
            orderEvent.CausationId,
            orderEvent.PreviousEventHash,
            orderEvent.Instruction,
            orderEvent.RiskDecision,
            orderEvent.BrokerOrderId,
            orderEvent.ExchangeOrderId,
            orderEvent.Fill,
            orderEvent.ReplacementTerms,
            orderEvent.Reconciliation,
            orderEvent.Reason);

    internal static string Compute(
        ClientOrderId aggregateId,
        long aggregateSequence,
        OrderEventKind kind,
        OrderLifecycleState? stateBefore,
        OrderLifecycleState stateAfter,
        OrderEventSource source,
        DeduplicationKey deduplicationKey,
        DateTime occurredAtUtc,
        DateTime recordedAtUtc,
        CausationId causationId,
        string previousEventHash,
        CanonicalOrderInstruction? instruction,
        RiskDecisionRecord? riskDecision,
        BrokerOrderId? brokerOrderId,
        ExchangeOrderId? exchangeOrderId,
        FillExecution? fill,
        CanonicalOrderTerms? replacementTerms,
        ReconciliationResolution? reconciliation,
        string? reason)
    {
        var canonical = new StringBuilder(1024);
        Append(canonical, "oms-order-event-v2");
        Append(canonical, aggregateId.Value);
        Append(canonical, aggregateSequence);
        Append(canonical, (int)kind);
        Append(canonical, stateBefore.HasValue ? (int)stateBefore.Value : -1);
        Append(canonical, (int)stateAfter);
        Append(canonical, (int)source);
        Append(canonical, deduplicationKey.Value);
        Append(canonical, occurredAtUtc.Ticks);
        Append(canonical, (int)occurredAtUtc.Kind);
        Append(canonical, recordedAtUtc.Ticks);
        Append(canonical, (int)recordedAtUtc.Kind);
        Append(canonical, causationId.Value);
        Append(canonical, previousEventHash);
        AppendInstruction(canonical, instruction);
        AppendRiskDecision(canonical, riskDecision);
        AppendOptional(canonical, brokerOrderId?.Value);
        AppendOptional(canonical, exchangeOrderId?.Value);
        AppendFill(canonical, fill);
        AppendTerms(canonical, replacementTerms);
        AppendReconciliation(canonical, reconciliation);
        AppendOptional(canonical, reason);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendInstruction(StringBuilder target, CanonicalOrderInstruction? instruction)
    {
        Append(target, instruction is null ? 0 : 1);
        if (instruction is null)
            return;

        var identity = instruction.Identity;
        Append(target, identity.IntentId.Value);
        AppendOptional(target, identity.BucketId?.Value);
        Append(target, identity.LegId.Value);
        Append(target, identity.ClientOrderId.Value);
        AppendOptional(target, identity.BrokerOrderId?.Value);
        AppendOptional(target, identity.ExchangeOrderId?.Value);
        Append(target, identity.CorrelationId.Value);
        Append(target, identity.CausationId.Value);
        Append(target, identity.ExecutionLeaseId.Value);
        Append(target, identity.FencingToken.Value);
        AppendTradeIntent(target, instruction.TradeIntent);
        AppendTerms(target, instruction.Terms);
    }

    private static void AppendTradeIntent(StringBuilder target, in TradeIntent intent)
    {
        Append(target, intent.Instrument.Value);
        Append(target, (int)intent.QuantityMode);
        Append(target, intent.SignedUnits.Coefficient);
        Append(target, intent.SignedUnits.Scale);
        AppendPrice(target, intent.ProtectiveStopPrice);
        AppendPrice(target, intent.ProfitTargetPrice);
        // Entry price terms are covered like every other recorded field: an altered limit price must
        // not survive chain verification. The domain separator above moves to v2 because this
        // changes the canonical layout - ledgers written under v1 will not re-verify, which is a
        // deliberate and loud break rather than a silent one.
        AppendPrice(target, intent.EntryLimitPrice);
        AppendPrice(target, intent.EntryStopPrice);
        Append(target, intent.EstimatedRoundTripCostPerUnit.Coefficient);
        Append(target, intent.EstimatedRoundTripCostPerUnit.Scale);
        Append(target, intent.StrategyId);
        Append(target, intent.StrategyNoteId);
        Append(target, intent.PolicyVersion);
    }

    private static void AppendRiskDecision(StringBuilder target, RiskDecisionRecord? decision)
    {
        Append(target, decision.HasValue ? 1 : 0);
        if (!decision.HasValue)
            return;

        var value = decision.Value;
        Append(target, value.PolicyId);
        Append(target, value.PolicyVersion);
        Append(target, value.PolicyHash);
        AppendLimits(target, value.PolicyLimits);
        AppendTradeIntent(target, value.Input.Intent);
        AppendQuantity(target, value.Input.PositionBefore);
        AppendPrice(target, value.Input.ReferencePrice);
        Append(target, value.Input.ContractMultiplier.Coefficient);
        Append(target, value.Input.ContractMultiplier.Scale);
        AppendMoney(target, value.Input.GrossExposureBefore);
        AppendMoney(target, value.Input.DailyRealizedPnl);
        AppendMoney(target, value.Input.DailyMarkToMarketPnl);
        Append(target, value.Input.RiskDay.DayNumber);
        Append(target, value.Input.IsComplete ? 1 : 0);
        Append(target, (int)value.Outcome);
        Append(target, (int)value.ReasonCodes);
        AppendQuantity(target, value.SignedOrderQuantity);
        AppendMoney(target, value.OrderNotional);
        AppendExposure(target, value.ExposureBefore);
        AppendExposure(target, value.ExposureAfter);
    }

    private static void AppendLimits(StringBuilder target, in RiskLimits limits)
    {
        AppendQuantity(target, limits.MaximumOrderQuantity);
        AppendMoney(target, limits.MaximumOrderNotional);
        AppendQuantity(target, limits.MaximumAbsolutePositionPerInstrument);
        AppendMoney(target, limits.MaximumGrossExposure);
        AppendMoney(target, limits.DailyLossLimit);
    }

    private static void AppendExposure(StringBuilder target, in RiskExposureSnapshot exposure)
    {
        AppendQuantity(target, exposure.Position);
        AppendMoney(target, exposure.InstrumentExposure);
        AppendMoney(target, exposure.GrossExposure);
    }

    private static void AppendFill(StringBuilder target, FillExecution? fill)
    {
        Append(target, fill.HasValue ? 1 : 0);
        if (!fill.HasValue)
            return;

        AppendQuantity(target, fill.Value.Quantity);
        AppendPrice(target, fill.Value.Price);
        AppendMoney(target, fill.Value.Fee);
        Append(target, (int)fill.Value.Liquidity);
    }

    private static void AppendTerms(StringBuilder target, CanonicalOrderTerms? terms)
    {
        Append(target, terms.HasValue ? 1 : 0);
        if (terms.HasValue)
            AppendTerms(target, terms.Value);
    }

    private static void AppendTerms(StringBuilder target, in CanonicalOrderTerms terms)
    {
        Append(target, (int)terms.Side);
        Append(target, (int)terms.OrderType);
        Append(target, (int)terms.TimeInForce);
        AppendQuantity(target, terms.Quantity);
        AppendPrice(target, terms.LimitPrice);
        AppendPrice(target, terms.StopPrice);
    }

    private static void AppendReconciliation(StringBuilder target, ReconciliationResolution? resolution)
    {
        Append(target, resolution.HasValue ? 1 : 0);
        if (!resolution.HasValue)
            return;

        Append(target, resolution.Value.CaseId.Value);
        Append(target, (int)resolution.Value.ObservedState);
        Append(target, resolution.Value.Evidence);
    }

    private static void AppendQuantity(StringBuilder target, in ScaledQuantity value)
    {
        Append(target, value.Coefficient);
        Append(target, value.Scale);
    }

    private static void AppendPrice(StringBuilder target, ScaledPrice? value)
    {
        Append(target, value.HasValue ? 1 : 0);
        if (value.HasValue)
            AppendPrice(target, value.Value);
    }

    private static void AppendPrice(StringBuilder target, in ScaledPrice value)
    {
        Append(target, value.Coefficient);
        Append(target, value.Scale);
    }

    private static void AppendMoney(StringBuilder target, in ScaledMoney value)
    {
        Append(target, value.Coefficient);
        Append(target, value.Scale);
    }

    private static void AppendOptional(StringBuilder target, string? value)
    {
        Append(target, value is null ? 0 : 1);
        if (value is not null)
            Append(target, value);
    }

    private static void Append(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
        target.Append('|');
    }

    private static void Append(StringBuilder target, long value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder target, int value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder target, byte value) =>
        Append(target, value.ToString(CultureInfo.InvariantCulture));
}
