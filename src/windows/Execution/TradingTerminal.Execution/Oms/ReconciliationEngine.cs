using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution.Oms;

/// <summary>Explicit, caller-driven reasons for a deterministic reconciliation cycle.</summary>
public enum ReconciliationTrigger : byte
{
    /// <summary>Durable engine startup or restart recovery.</summary>
    Startup = 0,

    /// <summary>The simulated execution session reconnected.</summary>
    Reconnect = 1,

    /// <summary>A coordinator command outcome became Unknown.</summary>
    UnknownOutcome = 2,

    /// <summary>A host-owned periodic cadence requested a cycle.</summary>
    Periodic = 3,

    /// <summary>An authenticated operator explicitly requested a cycle.</summary>
    OperatorRequest = 4,
}

/// <summary>Fault-as-value result for one reconciliation cycle.</summary>
public enum ReconciliationCycleFault : byte
{
    /// <summary>The cycle completed and every observation was durably represented.</summary>
    None = 0,

    /// <summary>The trigger, account, local projection set, or adapter snapshot was invalid.</summary>
    InvalidInput = 1,

    /// <summary>A case fact could not be appended to the configured store.</summary>
    PersistenceRejected = 2,

    /// <summary>An Unknown order could not accept otherwise valid snapshot resolution evidence.</summary>
    UnknownResolutionRejected = 3,

    /// <summary>Exact local position or cash arithmetic exceeded the supported ScaledValues range.</summary>
    ProjectionRejected = 4,
}

/// <summary>Immutable result of one synchronous, timer-free cycle.</summary>
public sealed record ReconciliationCycleResult(
    ReconciliationCycleFault Fault,
    ReconciliationTrigger Trigger,
    BrokerExecutionAccount Account,
    DateTime CompletedAtUtc,
    IReadOnlyList<ReconciliationCase> Cases,
    int UnresolvedMaterialCaseCount,
    string? Reason = null)
{
    /// <summary>Gets whether all comparisons and durable writes completed.</summary>
    public bool IsSuccess => Fault == ReconciliationCycleFault.None;

    /// <summary>Gets whether this account remains closed to new submit/replace admissions.</summary>
    public bool IsAdmissionBlocked => Fault != ReconciliationCycleFault.None || UnresolvedMaterialCaseCount > 0;
}

/// <summary>
/// Immutable ledger opening balance used to reconcile a real broker account. The default remains
/// the simulation account's zero SIM balance. Available cash can be observational only when a venue
/// reports margin buying power that cannot be derived exactly from fills.
/// </summary>
public sealed record ReconciliationCashBasis(
    string Currency,
    ScaledMoney OpeningTotal,
    ScaledMoney OpeningAvailable,
    bool CompareAvailable = true)
{
    /// <summary>The legacy deterministic-simulation basis.</summary>
    public static ReconciliationCashBasis SimulationZero { get; } =
        new("SIM", ScaledMoney.Zero, ScaledMoney.Zero);

    /// <summary>Whether the basis is bounded and exactly representable.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Currency) &&
        Currency.Length <= 32 &&
        string.Equals(Currency, Currency.Trim(), StringComparison.Ordinal) &&
        OpeningTotal.IsValid &&
        OpeningAvailable.IsValid;
}

/// <summary>
/// Deterministic in-process reconciliation against an adapter snapshot. The host supplies the
/// account's ledger projections and invokes startup/reconnect/periodic/operator triggers; there is
/// no timer, UI, IPC, broker SDK, socket, network, or live-order path.
/// </summary>
public sealed class ReconciliationEngine
{
    private const string SystemResolver = "system:reconciliation-engine";
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly OrderManagementService _oms;
    private readonly IReconciliationCaseStore _caseStore;
    private readonly IClock _clock;
    private readonly ReconciliationCashBasis _cashBasis;
    private readonly HashSet<BrokerExecutionAccount> _failedAccounts = [];
    private long _cycleSequence;

    /// <summary>Creates a clocked engine over the OMS ledger and append-only case store.</summary>
    public ReconciliationEngine(
        OrderManagementService oms,
        IReconciliationCaseStore caseStore,
        IClock clock,
        ReconciliationCashBasis? cashBasis = null)
    {
        _oms = oms ?? throw new ArgumentNullException(nameof(oms));
        _caseStore = caseStore ?? throw new ArgumentNullException(nameof(caseStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cashBasis = cashBasis ?? ReconciliationCashBasis.SimulationZero;
        if (!_cashBasis.IsValid)
            throw new ArgumentException("The reconciliation cash basis is invalid.", nameof(cashBasis));
    }

    /// <summary>
    /// Gets whether new submit/replace admission is open for an account. Cancel remains outside this
    /// gate; reduce-only is deliberately not admitted because disputed position truth cannot prove it.
    /// </summary>
    public bool CanAdmitNewOrders(BrokerExecutionAccount account)
    {
        if (!account.IsValid)
            return false;
        lock (_gate)
        {
            return !_failedAccounts.Contains(account) &&
                   CountUnresolvedMaterialCases(_caseStore.Read(account)) == 0;
        }
    }

    /// <summary>
    /// Closes the account admission gate when snapshot acquisition or coordinator-side cycle
    /// orchestration fails before a comparison can complete. Only a later successful cycle reopens it.
    /// </summary>
    public void FailClosed(BrokerExecutionAccount account) => MarkFailed(account);

    /// <summary>
    /// Compares exact local orders, fill-derived positions, and fill-derived SIM cash against one
    /// point-in-time simulated-adapter snapshot. Equivalent decimal encodings compare numerically
    /// exactly; no floating point or tolerance is used.
    /// </summary>
    public ReconciliationCycleResult RunCycle(
        ReconciliationTrigger trigger,
        BrokerExecutionAccount account,
        IReadOnlyList<OrderProjection> localOrders,
        BrokerReconciliationSnapshot brokerSnapshot)
    {
        var now = UtcNow();
        if (!Enum.IsDefined(trigger) ||
            !account.IsValid ||
            localOrders is null ||
            brokerSnapshot is null ||
            brokerSnapshot.Account != account)
        {
            MarkFailed(account);
            return Failed(
                ReconciliationCycleFault.InvalidInput,
                trigger,
                account,
                now,
                "The cycle trigger, account, local projection set, or adapter snapshot is invalid.");
        }

        lock (_gate)
        {
            var cycleSequence = NextCycleSequence();
            var cycleToken = $"{now.Ticks}:{cycleSequence}:{(byte)trigger}";
            var observations = new List<Observation>();
            var comparedSubjects = new HashSet<SubjectIdentity>();
            var snapshotCanResolveUnknown = false;

            if (!TryValidateSnapshot(brokerSnapshot, now, out var snapshotIssue))
            {
                observations.Add(new Observation(
                    ReconciliationSubjectKind.Account,
                    AccountSubjectKey(account),
                    null,
                    ReconciliationCaseKind.ManualException,
                    "v1:local-ledger-available",
                    $"v1:invalid-adapter-snapshot:{snapshotIssue}"));
            }
            else if (!TryCompare(
                         localOrders,
                         brokerSnapshot,
                         observations,
                         comparedSubjects,
                         out var projectionIssue))
            {
                observations.Add(new Observation(
                    ReconciliationSubjectKind.Account,
                    AccountSubjectKey(account),
                    null,
                    ReconciliationCaseKind.ManualException,
                    "v1:local-projection-invalid",
                    $"v1:adapter-snapshot-captured:{brokerSnapshot.CapturedAtUtc.Ticks}:{projectionIssue}"));
            }
            else
            {
                snapshotCanResolveUnknown = true;
                comparedSubjects.Add(new SubjectIdentity(
                    ReconciliationSubjectKind.Account,
                    AccountSubjectKey(account)));
            }

            var cycleCases = new List<ReconciliationCase>(observations.Count + 4);
            foreach (var observation in observations)
            {
                comparedSubjects.Add(observation.Subject);
                var persisted = OpenOrReuseCase(account, observation, now, cycleToken, cycleCases.Count);
                if (persisted is null)
                {
                    _failedAccounts.Add(account);
                    return Failed(
                        ReconciliationCycleFault.PersistenceRejected,
                        trigger,
                        account,
                        now,
                        "A reconciliation observation could not be appended.",
                        cycleCases);
                }
                cycleCases.Add(persisted);
            }

            if (!ResolveClearedCases(
                    account,
                    observations,
                    comparedSubjects,
                    brokerSnapshot.CapturedAtUtc,
                    now,
                    cycleCases))
            {
                _failedAccounts.Add(account);
                return Failed(
                    ReconciliationCycleFault.PersistenceRejected,
                    trigger,
                    account,
                    now,
                    "A cleared discrepancy could not append its resolution fact.",
                    cycleCases);
            }

            string? unknownReason = null;
            var unknownFault = snapshotCanResolveUnknown
                ? ResolveUnknownOrders(
                    account,
                    localOrders,
                    brokerSnapshot,
                    now,
                    cycleToken,
                    cycleCases,
                    out unknownReason)
                : ReconciliationCycleFault.None;
            if (unknownFault != ReconciliationCycleFault.None)
            {
                _failedAccounts.Add(account);
                return Failed(
                    unknownFault,
                    trigger,
                    account,
                    now,
                    unknownReason,
                    cycleCases);
            }

            _failedAccounts.Remove(account);
            var unresolved = CountUnresolvedMaterialCases(_caseStore.Read(account));
            return new ReconciliationCycleResult(
                ReconciliationCycleFault.None,
                trigger,
                account,
                now,
                new ReadOnlyCollection<ReconciliationCase>(cycleCases),
                unresolved);
        }
    }

    /// <summary>Appends an explicit operator resolution without changing the original observation.</summary>
    public bool ResolveCase(
        ReconciliationCaseId caseId,
        string operatorIdentity,
        string resolutionEvidence)
    {
        if (!caseId.IsValid ||
            string.IsNullOrWhiteSpace(operatorIdentity) ||
            operatorIdentity.Length > 256 ||
            string.IsNullOrWhiteSpace(resolutionEvidence))
        {
            return false;
        }

        lock (_gate)
        {
            var facts = _caseStore.Read(caseId);
            if (facts.Count == 0)
                return false;
            var latest = facts[^1];
            if (!latest.IsMaterial || latest.Status == ReconciliationCaseStatus.Resolved)
                return false;
            var resolved = latest with
            {
                Status = ReconciliationCaseStatus.Resolved,
                ResolvedAtUtc = UtcNow(),
                ResolvedBy = operatorIdentity.Trim(),
                ResolutionEvidence = resolutionEvidence,
            };
            if (!_caseStore.TryAppend(resolved))
                return false;
            return true;
        }
    }

    private bool TryCompare(
        IReadOnlyList<OrderProjection> localOrders,
        BrokerReconciliationSnapshot snapshot,
        List<Observation> observations,
        HashSet<SubjectIdentity> comparedSubjects,
        out string? issue)
    {
        issue = null;
        var localGroups = localOrders.GroupBy(item => item.ClientOrderId).ToArray();
        var brokerOrders = snapshot.OpenOrders.Concat(snapshot.CompletedOrders).ToArray();
        var brokerGroups = brokerOrders
            .GroupBy(item => item.Instruction.Identity.ClientOrderId)
            .ToDictionary(item => item.Key, item => item.ToArray());

        foreach (var duplicate in localGroups.Where(item => item.Count() > 1))
        {
            observations.Add(OrderObservation(
                duplicate.Key,
                ReconciliationCaseKind.DuplicateCandidate,
                SerializeOrders(duplicate),
                "v1:not-compared"));
        }

        foreach (var duplicate in brokerGroups.Where(item => item.Value.Length > 1))
        {
            observations.Add(OrderObservation(
                duplicate.Key,
                ReconciliationCaseKind.DuplicateCandidate,
                "v1:not-compared",
                SerializeVenueOrders(duplicate.Value)));
        }

        var duplicateBrokerOrderIds = brokerOrders
            .Where(item => item.BrokerOrderId.HasValue)
            .GroupBy(item => item.BrokerOrderId!.Value)
            .Where(item => item.Select(order => order.Instruction.Identity.ClientOrderId).Distinct().Count() > 1)
            .SelectMany(item => item)
            .Select(item => item.Instruction.Identity.ClientOrderId)
            .ToHashSet();
        var duplicateBrokerExchangeIds = brokerOrders
            .Where(item => item.ExchangeOrderId.HasValue)
            .GroupBy(item => item.ExchangeOrderId!.Value)
            .Where(item => item.Select(order => order.Instruction.Identity.ClientOrderId).Distinct().Count() > 1)
            .SelectMany(item => item)
            .Select(item => item.Instruction.Identity.ClientOrderId)
            .ToHashSet();
        var duplicateLocalBrokerOrderIds = localOrders
            .Where(item => item.BrokerOrderId.HasValue)
            .GroupBy(item => item.BrokerOrderId!.Value)
            .Where(item => item.Select(order => order.ClientOrderId).Distinct().Count() > 1)
            .SelectMany(item => item)
            .Select(item => item.ClientOrderId)
            .ToHashSet();
        var duplicateLocalExchangeIds = localOrders
            .Where(item => item.ExchangeOrderId.HasValue)
            .GroupBy(item => item.ExchangeOrderId!.Value)
            .Where(item => item.Select(order => order.ClientOrderId).Distinct().Count() > 1)
            .SelectMany(item => item)
            .Select(item => item.ClientOrderId)
            .ToHashSet();
        var duplicateNativeIds = duplicateBrokerOrderIds
            .Concat(duplicateBrokerExchangeIds)
            .Concat(duplicateLocalBrokerOrderIds)
            .Concat(duplicateLocalExchangeIds)
            .ToHashSet();
        foreach (var clientOrderId in duplicateNativeIds.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            var localKinds = string.Join(
                ',',
                new[]
                {
                    duplicateLocalBrokerOrderIds.Contains(clientOrderId) ? "broker-order-id" : null,
                    duplicateLocalExchangeIds.Contains(clientOrderId) ? "exchange-order-id" : null,
                }.Where(item => item is not null));
            var brokerKinds = string.Join(
                ',',
                new[]
                {
                    duplicateBrokerOrderIds.Contains(clientOrderId) ? "broker-order-id" : null,
                    duplicateBrokerExchangeIds.Contains(clientOrderId) ? "exchange-order-id" : null,
                }.Where(item => item is not null));
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.DuplicateCandidate,
                localKinds.Length == 0
                    ? "v1:not-compared"
                    : $"v1:duplicate-local-native-id:{localKinds};{SerializeOrder(localOrders.First(item => item.ClientOrderId == clientOrderId))}",
                brokerKinds.Length == 0
                    ? "v1:not-compared"
                    : $"v1:duplicate-broker-native-id:{brokerKinds};{SerializeVenueOrders(brokerGroups[clientOrderId])}"));
        }

        var localById = localGroups
            .Where(item => item.Count() == 1)
            .ToDictionary(item => item.Key, item => item.Single());
        var invalidMembershipIds = new HashSet<ClientOrderId>();
        var openIds = snapshot.OpenOrders
            .Select(item => item.Instruction.Identity.ClientOrderId)
            .ToHashSet();
        foreach (var pair in brokerGroups.Where(item => item.Value.Length == 1))
        {
            var broker = pair.Value[0];
            var isOpenCollection = openIds.Contains(pair.Key);
            if (isOpenCollection == !OrderLifecycle.IsTerminal(broker.State))
                continue;

            localById.TryGetValue(pair.Key, out var local);
            observations.Add(OrderObservation(
                pair.Key,
                ReconciliationCaseKind.TerminalStateMismatch,
                SerializeOrder(local),
                $"v1:collection={(isOpenCollection ? "open" : "completed")};{SerializeVenueOrder(broker)}"));
            invalidMembershipIds.Add(pair.Key);
        }
        var orderIds = localById.Keys
            .Concat(brokerGroups.Keys)
            .Distinct()
            .OrderBy(item => item.Value, StringComparer.Ordinal);
        foreach (var clientOrderId in orderIds)
        {
            if (localGroups.Any(item => item.Key == clientOrderId && item.Count() > 1) ||
                brokerGroups.TryGetValue(clientOrderId, out var candidates) && candidates.Length > 1 ||
                duplicateNativeIds.Contains(clientOrderId) ||
                invalidMembershipIds.Contains(clientOrderId))
            {
                continue;
            }

            localById.TryGetValue(clientOrderId, out var local);
            var broker = candidates?.SingleOrDefault();
            CompareOrder(local, broker, clientOrderId, observations);
        }

        if (!TryBuildLocalPositions(localOrders, out var localPositions, out issue) ||
            !TryBuildLocalCash(localOrders, out var localCash, out issue))
        {
            return false;
        }

        ComparePositions(localPositions, snapshot.Positions, observations);
        CompareCash(localCash, snapshot.Cash, _cashBasis, observations);
        foreach (var observation in observations)
            comparedSubjects.Add(observation.Subject);
        return true;
    }

    private void CompareOrder(
        OrderProjection? local,
        VenueOrderSnapshot? broker,
        ClientOrderId clientOrderId,
        List<Observation> observations)
    {
        var localEvidence = SerializeOrder(local);
        var brokerEvidence = SerializeVenueOrder(broker);
        if (local is null)
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.LocallyMissing,
                localEvidence,
                brokerEvidence));
            return;
        }
        if (broker is null)
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.BrokerMissing,
                localEvidence,
                brokerEvidence));
            return;
        }

        var differences = 0;
        if (!ExactEquals(local.Terms.Quantity, broker.CurrentTerms.Quantity) ||
            !ExactEquals(local.FilledQuantity, broker.FilledQuantity))
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.QuantityMismatch,
                localEvidence,
                brokerEvidence));
            differences++;
        }
        if (!ExactEquals(local.Terms.LimitPrice, broker.CurrentTerms.LimitPrice) ||
            !ExactEquals(local.Terms.StopPrice, broker.CurrentTerms.StopPrice))
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.PriceMismatch,
                localEvidence,
                brokerEvidence));
            differences++;
        }

        if (!EquivalentInstruction(local.Instruction, broker.Instruction) ||
            local.Terms.Side != broker.CurrentTerms.Side ||
            local.Terms.OrderType != broker.CurrentTerms.OrderType ||
            local.Terms.TimeInForce != broker.CurrentTerms.TimeInForce ||
            local.BrokerOrderId != broker.BrokerOrderId ||
            local.ExchangeOrderId != broker.ExchangeOrderId)
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.ManualException,
                localEvidence,
                brokerEvidence));
            differences++;
        }

        var effectiveLocalState = EffectiveLocalState(local);
        if (effectiveLocalState is OrderLifecycleState.Unknown or OrderLifecycleState.Reconciling &&
            effectiveLocalState == broker.State)
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.ManualException,
                localEvidence,
                brokerEvidence));
            differences++;
        }
        else if (effectiveLocalState != broker.State)
        {
            var kind = IsTerminalEconomicState(effectiveLocalState) || IsTerminalEconomicState(broker.State)
                ? ReconciliationCaseKind.TerminalStateMismatch
                : ReconciliationCaseKind.ManualException;
            observations.Add(OrderObservation(clientOrderId, kind, localEvidence, brokerEvidence));
            differences++;
        }

        if (differences == 0)
        {
            observations.Add(OrderObservation(
                clientOrderId,
                ReconciliationCaseKind.Matched,
                localEvidence,
                brokerEvidence));
        }
    }

    private static void ComparePositions(
        IReadOnlyDictionary<InstrumentId, ScaledQuantity> localPositions,
        IReadOnlyList<BrokerPositionSnapshot> brokerPositions,
        List<Observation> observations)
    {
        var brokerGroups = brokerPositions.GroupBy(item => item.Instrument).ToArray();
        foreach (var duplicate in brokerGroups.Where(item => item.Count() > 1))
        {
            var key = InstrumentKey(duplicate.Key);
            observations.Add(new Observation(
                ReconciliationSubjectKind.Position,
                key,
                null,
                ReconciliationCaseKind.DuplicateCandidate,
                "v1:not-compared",
                JsonSerializer.Serialize(duplicate.Select(SerializePosition))));
        }

        var brokerByInstrument = brokerGroups
            .Where(item => item.Count() == 1)
            .ToDictionary(item => item.Key, item => item.Single());
        var instruments = localPositions.Keys.Concat(brokerByInstrument.Keys).Distinct()
            .OrderBy(InstrumentKey, StringComparer.Ordinal);
        foreach (var instrument in instruments)
        {
            if (brokerGroups.Any(item => item.Key == instrument && item.Count() > 1))
                continue;
            var hasLocal = localPositions.TryGetValue(instrument, out var local);
            var hasBroker = brokerByInstrument.TryGetValue(instrument, out var broker);
            var brokerIsExplicitZero = hasBroker && ExactEquals(broker!.Quantity, ScaledQuantity.Zero);
            var localEvidence = hasLocal
                ? SerializePosition(instrument, local)
                : brokerIsExplicitZero
                    ? SerializePosition(instrument, ScaledQuantity.Zero)
                    : "v1:absent";
            var brokerEvidence = hasBroker ? SerializePosition(broker!) : "v1:absent";
            var kind = !hasLocal && brokerIsExplicitZero
                ? ReconciliationCaseKind.Matched
                : !hasLocal
                ? ReconciliationCaseKind.LocallyMissing
                : !hasBroker
                    ? ReconciliationCaseKind.BrokerMissing
                    : ExactEquals(local, broker!.Quantity)
                        ? ReconciliationCaseKind.Matched
                        : ReconciliationCaseKind.QuantityMismatch;
            observations.Add(new Observation(
                ReconciliationSubjectKind.Position,
                InstrumentKey(instrument),
                null,
                kind,
                localEvidence,
                brokerEvidence));
        }
    }

    private static void CompareCash(
        IReadOnlyDictionary<string, LocalCash> localCash,
        IReadOnlyList<BrokerCashSnapshot> brokerCash,
        ReconciliationCashBasis cashBasis,
        List<Observation> observations)
    {
        var brokerGroups = brokerCash.GroupBy(item => item.Currency, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in brokerGroups.Where(item => item.Count() > 1))
        {
            observations.Add(new Observation(
                ReconciliationSubjectKind.Cash,
                duplicate.Key,
                null,
                ReconciliationCaseKind.DuplicateCandidate,
                "v1:not-compared",
                JsonSerializer.Serialize(duplicate.Select(SerializeCash))));
        }

        var brokerByCurrency = brokerGroups
            .Where(item => item.Count() == 1)
            .ToDictionary(item => item.Key, item => item.Single(), StringComparer.Ordinal);
        var currencies = localCash.Keys.Concat(brokerByCurrency.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal);
        foreach (var currency in currencies)
        {
            if (brokerGroups.Any(item => string.Equals(item.Key, currency, StringComparison.Ordinal) && item.Count() > 1))
                continue;
            var hasLocal = localCash.TryGetValue(currency, out var local);
            var hasBroker = brokerByCurrency.TryGetValue(currency, out var broker);
            var localEvidence = hasLocal ? SerializeCash(currency, local) : "v1:absent";
            var brokerEvidence = hasBroker ? SerializeCash(broker!) : "v1:absent";
            var kind = !hasLocal
                ? ReconciliationCaseKind.LocallyMissing
                : !hasBroker
                    ? ReconciliationCaseKind.BrokerMissing
                    : ExactEquals(local.Total, broker!.Total) &&
                      (!cashBasis.CompareAvailable ||
                       !string.Equals(currency, cashBasis.Currency, StringComparison.Ordinal) ||
                       ExactEquals(local.Available, broker.Available))
                        ? ReconciliationCaseKind.Matched
                        : ReconciliationCaseKind.QuantityMismatch;
            observations.Add(new Observation(
                ReconciliationSubjectKind.Cash,
                currency,
                null,
                kind,
                localEvidence,
                brokerEvidence));
        }
    }

    private bool TryBuildLocalPositions(
        IReadOnlyList<OrderProjection> localOrders,
        out IReadOnlyDictionary<InstrumentId, ScaledQuantity> positions,
        out string? issue)
    {
        issue = null;
        var values = new Dictionary<InstrumentId, long>();
        foreach (var order in localOrders)
        {
            if (!order.FilledQuantity.TryGetWholeUnits(out var filled))
            {
                positions = new ReadOnlyDictionary<InstrumentId, ScaledQuantity>(new Dictionary<InstrumentId, ScaledQuantity>());
                issue = $"Order '{order.ClientOrderId}' has a non-integral filled quantity.";
                return false;
            }
            if (filled == 0)
                continue;
            var instrument = order.Instruction.TradeIntent.Instrument;
            values.TryGetValue(instrument, out var current);
            try
            {
                values[instrument] = checked(current + (order.Terms.Side == OrderSide.Buy ? filled : -filled));
            }
            catch (OverflowException)
            {
                positions = new ReadOnlyDictionary<InstrumentId, ScaledQuantity>(new Dictionary<InstrumentId, ScaledQuantity>());
                issue = $"Position arithmetic overflowed for '{InstrumentKey(instrument)}'.";
                return false;
            }
        }

        positions = new ReadOnlyDictionary<InstrumentId, ScaledQuantity>(
            values.Where(item => item.Value != 0)
                .ToDictionary(item => item.Key, item => ScaledQuantity.FromWhole(item.Value)));
        return true;
    }

    private bool TryBuildLocalCash(
        IReadOnlyList<OrderProjection> localOrders,
        out IReadOnlyDictionary<string, LocalCash> cash,
        out string? issue)
    {
        issue = null;
        Int128 total = _cashBasis.OpeningTotal.Coefficient;
        var totalScale = (int)_cashBasis.OpeningTotal.Scale;
        Int128 available = _cashBasis.OpeningAvailable.Coefficient;
        var availableScale = (int)_cashBasis.OpeningAvailable.Scale;
        foreach (var order in localOrders)
        {
            foreach (var orderEvent in _oms.ReadEvents(order.ClientOrderId))
            {
                if (orderEvent.Kind != OrderEventKind.FillReceived || orderEvent.Fill is not { } fill)
                    continue;
                if (!ScaledValueMath.TryMultiply(
                        fill.Quantity.Coefficient,
                        fill.Price.Coefficient,
                        out var notional))
                {
                    cash = EmptyCash();
                    issue = $"Cash notional overflowed for '{order.ClientOrderId}'.";
                    return false;
                }
                var notionalScale = fill.Quantity.Scale + fill.Price.Scale;
                if (order.Terms.Side == OrderSide.Buy)
                    notional = -notional;
                var fee = -(Int128)fill.Fee.Coefficient;
                if (!ScaledValueMath.TryAdd(
                        notional,
                        notionalScale,
                        fee,
                        fill.Fee.Scale,
                        out var afterFee,
                        out var afterFeeScale) ||
                    !ScaledValueMath.TryAdd(
                        total,
                        totalScale,
                        afterFee,
                        afterFeeScale,
                        out total,
                        out totalScale) ||
                    !ScaledValueMath.TryAdd(
                        available,
                        availableScale,
                        afterFee,
                        afterFeeScale,
                        out available,
                        out availableScale))
                {
                    cash = EmptyCash();
                    issue = $"Cash accumulation overflowed for '{order.ClientOrderId}'.";
                    return false;
                }
            }
        }

        if (!ScaledValueMath.TryNarrow(total, totalScale, out var totalCoefficient, out var totalResultScale) ||
            !ScaledValueMath.TryNarrow(
                available,
                availableScale,
                out var availableCoefficient,
                out var availableResultScale))
        {
            cash = EmptyCash();
            issue = "The exact cash projection cannot be represented as ScaledMoney.";
            return false;
        }
        var totalBalance = new ScaledMoney(totalCoefficient, totalResultScale);
        var availableBalance = new ScaledMoney(availableCoefficient, availableResultScale);
        cash = new ReadOnlyDictionary<string, LocalCash>(
            new Dictionary<string, LocalCash>(StringComparer.Ordinal)
            {
                [_cashBasis.Currency] = new LocalCash(totalBalance, availableBalance),
            });
        return true;
    }

    private ReconciliationCycleFault ResolveUnknownOrders(
        BrokerExecutionAccount account,
        IReadOnlyList<OrderProjection> localOrders,
        BrokerReconciliationSnapshot snapshot,
        DateTime now,
        string cycleToken,
        List<ReconciliationCase> cycleCases,
        out string? reason)
    {
        reason = null;
        var brokerById = snapshot.OpenOrders.Concat(snapshot.CompletedOrders)
            .GroupBy(item => item.Instruction.Identity.ClientOrderId)
            .Where(item => item.Count() == 1)
            .ToDictionary(item => item.Key, item => item.Single());
        var openOrderIds = snapshot.OpenOrders
            .Select(item => item.Instruction.Identity.ClientOrderId)
            .ToHashSet();
        foreach (var local in localOrders.Where(item => item.State == OrderLifecycleState.Unknown))
        {
            if (!brokerById.TryGetValue(local.ClientOrderId, out var broker) || broker.State == OrderLifecycleState.Unknown)
                continue;

            var unknownObservation = _oms.ReadEvents(local.ClientOrderId)
                .LastOrDefault(item => item.Kind == OrderEventKind.OutcomeUnknown);
            if (unknownObservation is null ||
                snapshot.CapturedAtUtc <= unknownObservation.RecordedAtUtc ||
                snapshot.CapturedAtUtc <= unknownObservation.OccurredAtUtc)
            {
                continue;
            }

            if (!CanResolveUnknownFromSnapshot(local, broker, openOrderIds.Contains(local.ClientOrderId)))
                continue;
            if (cycleCases.Any(item =>
                    item.ClientOrderId == local.ClientOrderId &&
                    item.Kind == ReconciliationCaseKind.DuplicateCandidate &&
                    item.Status != ReconciliationCaseStatus.Resolved))
            {
                continue;
            }

            var caseFacts = cycleCases
                .Where(item =>
                    item.ClientOrderId == local.ClientOrderId &&
                    item.Status != ReconciliationCaseStatus.Resolved &&
                    item.Kind is ReconciliationCaseKind.TerminalStateMismatch or ReconciliationCaseKind.ManualException)
                .GroupBy(item => item.CaseId)
                .Select(item => item.Last())
                .ToArray();
            if (caseFacts.Length == 0)
                continue;
            var resolutionCase = caseFacts.FirstOrDefault(item =>
                                     item.Kind == ReconciliationCaseKind.TerminalStateMismatch) ??
                                 caseFacts[0];

            var canResolveWorking = broker.State == OrderLifecycleState.Working &&
                                    ExactEquals(local.FilledQuantity, ScaledQuantity.Zero) &&
                                    ExactEquals(broker.FilledQuantity, ScaledQuantity.Zero);
            var canResolveTerminal = broker.State is OrderLifecycleState.Filled or
                OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired;
            if (!canResolveWorking && !canResolveTerminal)
                continue;

            var context = new OrderCommandContext(
                new CausationId($"reconciliation:{resolutionCase.CaseId.Value}"),
                new DeduplicationKey($"reconciliation:{Hash(cycleToken + local.ClientOrderId.Value)}"));
            var began = _oms.BeginReconciliation(local.ClientOrderId, context);
            if (!began.IsSuccess)
            {
                reason = began.Reason ?? $"Unknown order '{local.ClientOrderId}' could not enter reconciliation.";
                return ReconciliationCycleFault.UnknownResolutionRejected;
            }

            OmsCommandResult completed;
            if (canResolveWorking)
            {
                completed = _oms.ApplyVenueEvent(new VenueEvent(
                    VenueEventKind.Acknowledged,
                    local.ClientOrderId,
                    broker.BrokerOrderId,
                    broker.ExchangeOrderId,
                    null,
                    null,
                    snapshot.CapturedAtUtc,
                    context.CausationId,
                    context.DeduplicationKey.Derive("snapshot-acknowledged"),
                    "Resolved from the simulated adapter reconciliation snapshot."));
            }
            else if (canResolveTerminal)
            {
                completed = _oms.CompleteReconciliation(
                    local.ClientOrderId,
                    new ReconciliationResolution(
                        resolutionCase.CaseId,
                        broker.State,
                        Evidence: SerializeVenueOrder(broker)),
                    context,
                    broker.BrokerOrderId,
                    broker.ExchangeOrderId);
            }
            else
            {
                continue;
            }
            if (!completed.IsSuccess)
            {
                reason = completed.Reason ?? $"Unknown order '{local.ClientOrderId}' rejected snapshot resolution.";
                return ReconciliationCycleFault.UnknownResolutionRejected;
            }

            foreach (var caseFact in caseFacts)
            {
                var resolved = ResolveFact(
                    caseFact,
                    now,
                    SystemResolver,
                    $"OMS state resolved from the {snapshot.CapturedAtUtc:O} simulated-adapter snapshot as {broker.State}.");
                if (!_caseStore.TryAppend(resolved))
                {
                    reason = $"The resolved Unknown case '{caseFact.CaseId}' could not be appended.";
                    return ReconciliationCycleFault.PersistenceRejected;
                }
                ReplaceCase(cycleCases, caseFact, resolved);
            }
        }
        return ReconciliationCycleFault.None;
    }

    private ReconciliationCase? OpenOrReuseCase(
        BrokerExecutionAccount account,
        Observation observation,
        DateTime now,
        string cycleToken,
        int observationIndex)
    {
        var existing = LatestFacts(_caseStore.Read(account))
            .Where(item => item.Status != ReconciliationCaseStatus.Resolved)
            .FirstOrDefault(item =>
                item.SubjectKind == observation.SubjectKind &&
                string.Equals(item.SubjectKey, observation.SubjectKey, StringComparison.Ordinal) &&
                item.Kind == observation.Kind &&
                string.Equals(item.LocalEvidence, observation.LocalEvidence, StringComparison.Ordinal) &&
                string.Equals(item.BrokerEvidence, observation.BrokerEvidence, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        var existingFacts = _caseStore.Read(account);
        var caseSeed =
            $"{account.AdapterId.Value}|{account.AccountId.Value}|{observation.SubjectKind}|" +
            $"{observation.SubjectKey}|{observation.Kind}|{observation.LocalEvidence}|" +
            $"{observation.BrokerEvidence}|{cycleToken}|{observationIndex}";
        var collision = 0;
        ReconciliationCaseId caseId;
        do
        {
            caseId = new ReconciliationCaseId($"recon-{Hash($"{caseSeed}|{collision}")}");
            if (collision == int.MaxValue)
                throw new InvalidOperationException("The reconciliation-case identity space is exhausted.");
            collision++;
        }
        while (existingFacts.Any(item => item.CaseId == caseId));
        var matched = observation.Kind == ReconciliationCaseKind.Matched;
        var fact = new ReconciliationCase(
            caseId,
            account,
            observation.SubjectKind,
            observation.SubjectKey,
            observation.ClientOrderId,
            observation.Kind,
            matched ? ReconciliationCaseStatus.Resolved : ReconciliationCaseStatus.Open,
            observation.LocalEvidence,
            observation.BrokerEvidence,
            now,
            matched ? now : null,
            matched ? SystemResolver : null,
            matched ? "The exact local and simulated-adapter evidence matched." : null);
        return _caseStore.TryAppend(fact) ? fact : null;
    }

    private bool ResolveClearedCases(
        BrokerExecutionAccount account,
        IReadOnlyList<Observation> observations,
        IReadOnlySet<SubjectIdentity> comparedSubjects,
        DateTime snapshotCapturedAtUtc,
        DateTime now,
        List<ReconciliationCase> cycleCases)
    {
        var activeKinds = observations
            .Where(item => item.Kind != ReconciliationCaseKind.Matched)
            .Select(item => (item.Subject, item.Kind))
            .ToHashSet();
        foreach (var latest in LatestFacts(_caseStore.Read(account)))
        {
            if (!latest.IsMaterial ||
                latest.Status == ReconciliationCaseStatus.Resolved ||
                snapshotCapturedAtUtc <= latest.OpenedAtUtc ||
                !comparedSubjects.Contains(new SubjectIdentity(latest.SubjectKind, latest.SubjectKey)) ||
                activeKinds.Contains((new SubjectIdentity(latest.SubjectKind, latest.SubjectKey), latest.Kind)))
            {
                continue;
            }

            var resolved = ResolveFact(
                latest,
                now,
                SystemResolver,
                "A later exact reconciliation observation no longer contains this discrepancy.");
            if (!_caseStore.TryAppend(resolved))
                return false;
            ReplaceCase(cycleCases, latest, resolved);
        }
        return true;
    }

    private static ReconciliationCase ResolveFact(
        ReconciliationCase source,
        DateTime resolvedAtUtc,
        string resolvedBy,
        string evidence) =>
        source with
        {
            Status = ReconciliationCaseStatus.Resolved,
            ResolvedAtUtc = resolvedAtUtc,
            ResolvedBy = resolvedBy,
            ResolutionEvidence = evidence,
        };

    private static void ReplaceCase(
        List<ReconciliationCase> cycleCases,
        ReconciliationCase original,
        ReconciliationCase resolved)
    {
        var index = cycleCases.FindLastIndex(item => item.CaseId == original.CaseId);
        if (index >= 0)
            cycleCases[index] = resolved;
        else
            cycleCases.Add(resolved);
    }

    private static IReadOnlyList<ReconciliationCase> LatestFacts(IReadOnlyList<ReconciliationCase> facts) =>
        facts.GroupBy(item => item.CaseId)
            .Select(item => item.Last())
            .ToArray();

    private static int CountUnresolvedMaterialCases(IReadOnlyList<ReconciliationCase> facts) =>
        LatestFacts(facts).Count(item => item.IsMaterial && item.Status != ReconciliationCaseStatus.Resolved);

    private static bool TryValidateSnapshot(
        BrokerReconciliationSnapshot snapshot,
        DateTime cycleAtUtc,
        out string? issue)
    {
        issue = null;
        if (!snapshot.Account.IsValid || snapshot.CapturedAtUtc.Kind != DateTimeKind.Utc ||
            snapshot.CapturedAtUtc > cycleAtUtc ||
            cycleAtUtc - snapshot.CapturedAtUtc > MaximumSnapshotAge ||
            snapshot.OpenOrders is null || snapshot.CompletedOrders is null ||
            snapshot.Positions is null || snapshot.Cash is null)
        {
            issue = "The snapshot envelope is invalid.";
            return false;
        }
        foreach (var order in snapshot.OpenOrders.Concat(snapshot.CompletedOrders))
        {
            if (order is null || order.Instruction is null ||
                order.Instruction.Validate() != OrderDomainFault.None ||
                order.CurrentTerms.Validate() != OrderDomainFault.None ||
                !Enum.IsDefined(order.State) ||
                order.BrokerOrderId is { IsValid: false } ||
                order.ExchangeOrderId is { IsValid: false } ||
                !order.FilledQuantity.IsValid || order.FilledQuantity.Coefficient < 0 ||
                !HasCoherentFillState(order))
            {
                issue = "An adapter order snapshot is invalid.";
                return false;
            }
        }
        foreach (var position in snapshot.Positions)
        {
            if (position is null || position.Instrument.IsNone || !position.Quantity.IsValid ||
                position.ObservedAtUtc.Kind != DateTimeKind.Utc ||
                position.ObservedAtUtc > snapshot.CapturedAtUtc ||
                snapshot.CapturedAtUtc - position.ObservedAtUtc > MaximumSnapshotAge)
            {
                issue = "An adapter position snapshot is invalid.";
                return false;
            }
        }
        foreach (var item in snapshot.Cash)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Currency) || item.Currency.Length > 32 ||
                !item.Total.IsValid || !item.Available.IsValid || item.ObservedAtUtc.Kind != DateTimeKind.Utc ||
                item.ObservedAtUtc > snapshot.CapturedAtUtc ||
                snapshot.CapturedAtUtc - item.ObservedAtUtc > MaximumSnapshotAge)
            {
                issue = "An adapter cash snapshot is invalid.";
                return false;
            }
        }
        return true;
    }

    private OrderLifecycleState EffectiveLocalState(OrderProjection projection)
    {
        if (projection.State != OrderLifecycleState.Reconciled)
            return projection.State;
        var resolution = _oms.ReadEvents(projection.ClientOrderId)
            .LastOrDefault(item => item.Kind == OrderEventKind.Reconciled)
            ?.Reconciliation;
        return resolution?.ObservedState ?? projection.State;
    }

    private static bool IsTerminalEconomicState(OrderLifecycleState state) =>
        state is OrderLifecycleState.Filled or
            OrderLifecycleState.Cancelled or
            OrderLifecycleState.Rejected or
            OrderLifecycleState.Expired;

    private static bool EquivalentInstruction(
        CanonicalOrderInstruction local,
        CanonicalOrderInstruction broker)
    {
        var localIntent = local.TradeIntent;
        var brokerIntent = broker.TradeIntent;
        return local.Identity == broker.Identity &&
               localIntent.Instrument == brokerIntent.Instrument &&
               localIntent.QuantityMode == brokerIntent.QuantityMode &&
               ExactEquals(localIntent.SignedUnits, brokerIntent.SignedUnits) &&
               ExactEquals(localIntent.ProtectiveStopPrice, brokerIntent.ProtectiveStopPrice) &&
               ExactEquals(localIntent.ProfitTargetPrice, brokerIntent.ProfitTargetPrice) &&
               ExactEquals(
                   localIntent.EstimatedRoundTripCostPerUnit,
                   brokerIntent.EstimatedRoundTripCostPerUnit) &&
               string.Equals(localIntent.StrategyId, brokerIntent.StrategyId, StringComparison.Ordinal) &&
               localIntent.StrategyNoteId == brokerIntent.StrategyNoteId &&
               string.Equals(localIntent.PolicyVersion, brokerIntent.PolicyVersion, StringComparison.Ordinal) &&
               local.Terms.Side == broker.Terms.Side &&
               local.Terms.OrderType == broker.Terms.OrderType &&
               local.Terms.TimeInForce == broker.Terms.TimeInForce &&
               ExactEquals(local.Terms.Quantity, broker.Terms.Quantity) &&
               ExactEquals(local.Terms.LimitPrice, broker.Terms.LimitPrice) &&
               ExactEquals(local.Terms.StopPrice, broker.Terms.StopPrice);
    }

    private static bool CanResolveUnknownFromSnapshot(
        OrderProjection local,
        VenueOrderSnapshot broker,
        bool isInOpenCollection)
    {
        if (isInOpenCollection != !OrderLifecycle.IsTerminal(broker.State) ||
            !EquivalentInstruction(local.Instruction, broker.Instruction) ||
            local.Terms.Side != broker.CurrentTerms.Side ||
            local.Terms.OrderType != broker.CurrentTerms.OrderType ||
            local.Terms.TimeInForce != broker.CurrentTerms.TimeInForce ||
            !ExactEquals(local.Terms.Quantity, broker.CurrentTerms.Quantity) ||
            !ExactEquals(local.Terms.LimitPrice, broker.CurrentTerms.LimitPrice) ||
            !ExactEquals(local.Terms.StopPrice, broker.CurrentTerms.StopPrice) ||
            !ExactEquals(local.FilledQuantity, broker.FilledQuantity) ||
            local.BrokerOrderId.HasValue && local.BrokerOrderId != broker.BrokerOrderId ||
            local.ExchangeOrderId.HasValue && local.ExchangeOrderId != broker.ExchangeOrderId)
        {
            return false;
        }

        return broker.State == OrderLifecycleState.Working ||
               broker.State is OrderLifecycleState.Filled or
                   OrderLifecycleState.Cancelled or
                   OrderLifecycleState.Rejected or
                   OrderLifecycleState.Expired;
    }

    private static bool HasCoherentFillState(VenueOrderSnapshot order)
    {
        if (!TryCompareExact(order.FilledQuantity, order.CurrentTerms.Quantity, out var comparison) ||
            comparison > 0)
        {
            return false;
        }

        var isZero = ExactEquals(order.FilledQuantity, ScaledQuantity.Zero);
        var isFull = comparison == 0;
        return order.State switch
        {
            OrderLifecycleState.Acknowledging or OrderLifecycleState.Working => isZero,
            OrderLifecycleState.PartiallyFilled => !isZero && !isFull,
            OrderLifecycleState.Filled => isFull,
            OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired or
                OrderLifecycleState.PendingCancel or
                OrderLifecycleState.PendingReplace or
                OrderLifecycleState.Unknown => !isFull,
            _ => false,
        };
    }

    private static bool ExactEquals(ScaledQuantity left, ScaledQuantity right) =>
        ExactEquals(left.Coefficient, left.Scale, right.Coefficient, right.Scale);

    private static bool ExactEquals(ScaledMoney left, ScaledMoney right) =>
        ExactEquals(left.Coefficient, left.Scale, right.Coefficient, right.Scale);

    private static bool ExactEquals(ScaledPrice? left, ScaledPrice? right) =>
        !left.HasValue && !right.HasValue ||
        left.HasValue && right.HasValue && ExactEquals(
            left.Value.Coefficient,
            left.Value.Scale,
            right.Value.Coefficient,
            right.Value.Scale);

    private static bool TryCompareExact(
        ScaledQuantity left,
        ScaledQuantity right,
        out int comparison)
    {
        if (!ScaledValueMath.TryAlign(
                left.Coefficient,
                left.Scale,
                right.Coefficient,
                right.Scale,
                out var alignedLeft,
                out var alignedRight,
                out _))
        {
            comparison = 0;
            return false;
        }

        comparison = alignedLeft.CompareTo(alignedRight);
        return true;
    }

    private static bool ExactEquals(long left, byte leftScale, long right, byte rightScale) =>
        ScaledValueMath.TryAlign(
            left,
            leftScale,
            right,
            rightScale,
            out var alignedLeft,
            out var alignedRight,
            out _) && alignedLeft == alignedRight;

    private static Observation OrderObservation(
        ClientOrderId clientOrderId,
        ReconciliationCaseKind kind,
        string localEvidence,
        string brokerEvidence) =>
        new(
            ReconciliationSubjectKind.Order,
            clientOrderId.Value,
            clientOrderId,
            kind,
            localEvidence,
            brokerEvidence);

    private static string SerializeOrder(OrderProjection? order) => order is null
        ? "v1:absent"
        : JsonSerializer.Serialize(new
        {
            version = 1,
            clientOrderId = order.ClientOrderId.Value,
            state = order.State.ToString(),
            instrument = InstrumentKey(order.Instruction.TradeIntent.Instrument),
            side = order.Terms.Side.ToString(),
            orderType = order.Terms.OrderType.ToString(),
            timeInForce = order.Terms.TimeInForce.ToString(),
            quantity = Exact(order.Terms.Quantity.Coefficient, order.Terms.Quantity.Scale),
            filledQuantity = Exact(order.FilledQuantity.Coefficient, order.FilledQuantity.Scale),
            limitPrice = Exact(order.Terms.LimitPrice),
            stopPrice = Exact(order.Terms.StopPrice),
            brokerOrderId = order.BrokerOrderId?.Value,
            exchangeOrderId = order.ExchangeOrderId?.Value,
        });

    private static string SerializeOrders(IEnumerable<OrderProjection> orders) =>
        JsonSerializer.Serialize(orders.Select(SerializeOrder));

    private static string SerializeVenueOrder(VenueOrderSnapshot? order) => order is null
        ? "v1:absent"
        : JsonSerializer.Serialize(new
        {
            version = 1,
            clientOrderId = order.Instruction.Identity.ClientOrderId.Value,
            state = order.State.ToString(),
            instrument = InstrumentKey(order.Instruction.TradeIntent.Instrument),
            side = order.CurrentTerms.Side.ToString(),
            orderType = order.CurrentTerms.OrderType.ToString(),
            timeInForce = order.CurrentTerms.TimeInForce.ToString(),
            quantity = Exact(order.CurrentTerms.Quantity.Coefficient, order.CurrentTerms.Quantity.Scale),
            filledQuantity = Exact(order.FilledQuantity.Coefficient, order.FilledQuantity.Scale),
            limitPrice = Exact(order.CurrentTerms.LimitPrice),
            stopPrice = Exact(order.CurrentTerms.StopPrice),
            brokerOrderId = order.BrokerOrderId?.Value,
            exchangeOrderId = order.ExchangeOrderId?.Value,
        });

    private static string SerializeVenueOrders(IEnumerable<VenueOrderSnapshot> orders) =>
        JsonSerializer.Serialize(orders.Select(SerializeVenueOrder));

    private static string SerializePosition(InstrumentId instrument, ScaledQuantity quantity) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            instrument = InstrumentKey(instrument),
            quantity = Exact(quantity.Coefficient, quantity.Scale),
        });

    private static string SerializePosition(BrokerPositionSnapshot position) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            instrument = InstrumentKey(position.Instrument),
            quantity = Exact(position.Quantity.Coefficient, position.Quantity.Scale),
            observedAtUtc = position.ObservedAtUtc,
        });

    private static string SerializeCash(string currency, LocalCash cash) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            currency,
            total = Exact(cash.Total.Coefficient, cash.Total.Scale),
            available = Exact(cash.Available.Coefficient, cash.Available.Scale),
        });

    private static string SerializeCash(BrokerCashSnapshot cash) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            cash.Currency,
            total = Exact(cash.Total.Coefficient, cash.Total.Scale),
            available = Exact(cash.Available.Coefficient, cash.Available.Scale),
            observedAtUtc = cash.ObservedAtUtc,
        });

    private static string Exact(ScaledPrice? price) => price.HasValue
        ? Exact(price.Value.Coefficient, price.Value.Scale)
        : "none";

    private static string Exact(long coefficient, byte scale) => $"{coefficient}e-{scale}";

    private static string InstrumentKey(InstrumentId instrument) => instrument.ToString();

    private static string AccountSubjectKey(BrokerExecutionAccount account) =>
        $"{account.AdapterId.Value}/{account.AccountId.Value}";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private DateTime UtcNow()
    {
        var value = _clock.UtcNow;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private long NextCycleSequence()
    {
        if (_cycleSequence == long.MaxValue)
            throw new InvalidOperationException("The reconciliation-cycle sequence is exhausted.");
        return ++_cycleSequence;
    }

    private void MarkFailed(BrokerExecutionAccount account)
    {
        if (!account.IsValid)
            return;
        lock (_gate)
            _failedAccounts.Add(account);
    }

    private static IReadOnlyDictionary<string, LocalCash> EmptyCash() =>
        new ReadOnlyDictionary<string, LocalCash>(new Dictionary<string, LocalCash>(StringComparer.Ordinal));

    private static ReconciliationCycleResult Failed(
        ReconciliationCycleFault fault,
        ReconciliationTrigger trigger,
        BrokerExecutionAccount account,
        DateTime now,
        string? reason,
        IReadOnlyList<ReconciliationCase>? cases = null) =>
        new(
            fault,
            trigger,
            account,
            now,
            cases is null
                ? Array.Empty<ReconciliationCase>()
                : new ReadOnlyCollection<ReconciliationCase>(cases.ToArray()),
            1,
            reason);

    private readonly record struct SubjectIdentity(ReconciliationSubjectKind Kind, string Key);

    private sealed record Observation(
        ReconciliationSubjectKind SubjectKind,
        string SubjectKey,
        ClientOrderId? ClientOrderId,
        ReconciliationCaseKind Kind,
        string LocalEvidence,
        string BrokerEvidence)
    {
        internal SubjectIdentity Subject => new(SubjectKind, SubjectKey);
    }

    private readonly record struct LocalCash(ScaledMoney Total, ScaledMoney Available);
}
