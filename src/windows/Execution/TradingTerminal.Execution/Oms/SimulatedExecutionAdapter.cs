using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution.Oms;

/// <summary>Scheduler used to separate local dispatch from asynchronous adapter callbacks.</summary>
public interface IAdapterEventScheduler
{
    /// <summary>Queues one callback without invoking it inline.</summary>
    void Schedule(Action callback);
}

/// <summary>
/// Manually drained, deterministic scheduler for simulation and tests. It creates no timer, task,
/// worker thread, or race with the coordinator.
/// </summary>
public sealed class ControllableAdapterEventScheduler : IAdapterEventScheduler
{
    private readonly object _gate = new();
    private readonly Queue<Action> _callbacks = [];

    /// <summary>Gets the number of callbacks awaiting explicit delivery.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _callbacks.Count;
        }
    }

    /// <inheritdoc />
    public void Schedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
            _callbacks.Enqueue(callback);
    }

    /// <summary>Delivers the next callback, returning false when the queue is empty.</summary>
    public bool RunNext()
    {
        Action? callback;
        lock (_gate)
        {
            if (_callbacks.Count == 0)
                return false;
            callback = _callbacks.Dequeue();
        }

        callback();
        return true;
    }

    /// <summary>Delivers every callback in deterministic FIFO order, including newly queued work.</summary>
    public int RunAll()
    {
        var delivered = 0;
        while (RunNext())
            delivered++;
        return delivered;
    }
}

/// <summary>
/// The only slice-3 broker execution adapter. It composes the slice-1 deterministic venue, queues
/// every callback through an injected controllable scheduler, and contains no network path.
/// </summary>
public sealed class SimulatedExecutionAdapter : IBrokerExecutionAdapter
{
    private readonly object _gate = new();
    private readonly DeterministicSimulatedVenue _venue;
    private readonly IClock _clock;
    private readonly IAdapterEventScheduler _scheduler;
    private readonly bool _duplicateCallbacks;
    private readonly HashSet<ClientOrderId> _knownClientOrderIds = [];
    private readonly Dictionary<BrokerOrderId, ClientOrderId> _brokerToClient = [];
    private readonly Dictionary<InstrumentId, long> _positions = [];
    private readonly Dictionary<string, SimulatedCashBalance> _cash = new(StringComparer.Ordinal);

    /// <summary>
    /// The currency this adapter settles fills in — whatever it was opened with.
    ///
    /// <para>Fill accounting used to name <c>SIM</c> as a literal, which quietly tied the whole adapter
    /// to its own default opening balance. Open it in any other currency and every fill looked up a
    /// balance that was not there, took the early return, and silently posted nothing: orders filled,
    /// cash never moved, and a book that had gone long could never be brought back to flat. Nothing
    /// threw and nothing was logged. Found by seeding the paper account in dollars.</para>
    /// </summary>
    private readonly string _settlementCurrency;
    private readonly HashSet<DeduplicationKey> _accountedFillKeys = [];
    private BrokerReconciliationSnapshot? _reconciliationSnapshotOverride;
    private DateTime _rateWindowStartedUtc;
    private int _commandsInRateWindow;

    /// <summary>Creates a deterministic, execution-authenticated and certified simulation adapter.</summary>
    public SimulatedExecutionAdapter(
        DeterministicSimulatedVenue venue,
        IClock clock,
        IAdapterEventScheduler scheduler,
        BrokerExecutionSession? session = null,
        BrokerExecutionCapabilities? capabilities = null,
        bool duplicateCallbacks = false,
        IEnumerable<BrokerCashSnapshot>? cash = null)
    {
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _duplicateCallbacks = duplicateCallbacks;

        var observedAtUtc = UtcNow();
        Account = session?.Account ?? new BrokerExecutionAccount(
            new ExecutionAdapterId("simulated"),
            new BrokerAccountId("simulated-account"));
        Session = session ?? new BrokerExecutionSession(
            Account,
            ExecutionSessionHealth.Healthy,
            IsDataConnected: true,
            IsExecutionAuthenticated: true,
            IsExecutionCertified: true,
            observedAtUtc);
        if (!Account.IsValid || Session.Account != Account)
            throw new ArgumentException("The simulated session must identify one valid adapter/account.", nameof(session));

        Capabilities = capabilities ?? CreateDefaultCapabilities(venue.Capabilities);
        _rateWindowStartedUtc = observedAtUtc;
        var openingCash = cash?.ToArray() ??
        [
            new BrokerCashSnapshot("SIM", ScaledMoney.Zero, ScaledMoney.Zero, observedAtUtc),
        ];
        // The first opening balance is what fills settle against. One currency, because this adapter
        // fills against a single simulated venue rather than a multi-currency account.
        _settlementCurrency = openingCash.Length > 0 && !string.IsNullOrWhiteSpace(openingCash[0]?.Currency)
            ? openingCash[0]!.Currency
            : "SIM";

        foreach (var item in openingCash)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Currency) ||
                !item.Total.IsValid ||
                !item.Available.IsValid ||
                item.ObservedAtUtc.Kind != DateTimeKind.Utc ||
                !_cash.TryAdd(item.Currency, new SimulatedCashBalance(item.Total, item.Available)))
            {
                throw new ArgumentException("The simulated opening cash snapshot is invalid or duplicated.", nameof(cash));
            }
        }
    }

    /// <inheritdoc />
    public string BrokerId => "simulated";

    /// <inheritdoc />
    public ExecutionMode Mode => ExecutionMode.Paper;

    /// <inheritdoc />
    public BrokerExecutionAccount Account { get; }

    /// <inheritdoc />
    public BrokerExecutionSession Session { get; }

    /// <inheritdoc />
    public BrokerExecutionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public event Action<BrokerAdapterEvent>? EventReceived;

    /// <summary>Creates the default exact simulation capability set.</summary>
    public static BrokerExecutionCapabilities CreateDefaultCapabilities(VenueCapabilities canonical) =>
        new(
            Version: "sim-v1",
            CanonicalCapabilities: canonical,
            QuantityPrecision: 0,
            MinimumQuantity: ScaledQuantity.FromWhole(1),
            MaximumQuantity: ScaledQuantity.FromWhole(1_000_000),
            LotSize: ScaledQuantity.FromWhole(1),
            SupportsFractionalQuantity: false,
            PricePrecision: 8,
            TickSize: new ScaledPrice(1, 8),
            MinimumPrice: new ScaledPrice(1, 8),
            MaximumPrice: new ScaledPrice(1_000_000_000, 0),
            ReplaceSemantics: BrokerReplaceSemantics.InPlace,
            SupportsNativeBracket: false,
            SupportsNativeOco: false,
            TradingHours: BrokerTradingHours.AlwaysOpen,
            RateLimit: new BrokerRateLimit(1_000, TimeSpan.FromSeconds(1)));

    /// <inheritdoc />
    public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command)
    {
        if (command is null ||
            command.Instruction is null ||
            !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The submit command is invalid or uses a stale capability version.");
        }

        var admission = BrokerExecutionAdmission.Evaluate(
            Session,
            Capabilities,
            command.Instruction,
            UtcNow());
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The simulated account command rate limit was exceeded.");

        var venueResult = _venue.Submit(command.Instruction, command.CausationId);
        var clientOrderId = command.Instruction.Identity.ClientOrderId;
        lock (_gate)
        {
            _knownClientOrderIds.Add(clientOrderId);
            RememberBrokerId(venueResult.Order);
        }

        if (venueResult.Status == VenueCommandStatus.Conflict ||
            venueResult.Fault == VenueCommandFault.IdempotencyConflict)
        {
            return new BrokerAdapterCommandResult(
                BrokerAdapterCommandStatus.Conflict,
                BrokerAdapterCommandFault.Conflict,
                null,
                0,
                "The simulated venue reported an idempotency conflict.");
        }

        if (venueResult.EffectiveStatus == VenueCommandStatus.FailedBeforeAcceptance)
        {
            return new BrokerAdapterCommandResult(
                BrokerAdapterCommandStatus.RejectedBeforeDispatch,
                BrokerAdapterCommandFault.VenueRejected,
                null,
                0,
                "The simulated venue proved failure before acceptance.");
        }
        var scheduled = ScheduleVenueEvents(venueResult.Events, command.Instruction);
        if (venueResult.Events.Count == 0 && venueResult.EffectiveStatus == VenueCommandStatus.Rejected)
            return Rejected(MapFault(venueResult.Fault), $"The simulated venue rejected submit: {venueResult.Fault}.");

        var receipt = CreateReceipt(
            BrokerAdapterCommandKind.Submit,
            clientOrderId,
            command.CausationId);
        return Dispatched(receipt, scheduled);
    }

    /// <inheritdoc />
    public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid)
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The cancel command is invalid.");
        if (!Session.CanExecute)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The adapter session cannot execute.");
        if (!TryResolve(command.Order, out var clientOrderId, out var snapshot))
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching simulated order exists.");
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The simulated account command rate limit was exceeded.");

        var venueResult = _venue.Cancel(clientOrderId, command.CausationId);
        var scheduled = ScheduleVenueEvents(venueResult.Events, snapshot!.Instruction);
        if (venueResult.Events.Count == 0 && venueResult.EffectiveStatus == VenueCommandStatus.Rejected)
            return Rejected(MapFault(venueResult.Fault), $"The simulated venue rejected cancel: {venueResult.Fault}.");

        var receipt = CreateReceipt(BrokerAdapterCommandKind.Cancel, clientOrderId, command.CausationId);
        return Dispatched(receipt, scheduled);
    }

    /// <inheritdoc />
    public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
    {
        if (command is null ||
            !command.Order.IsValid ||
            !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The replace command is invalid or uses a stale capability version.");
        }
        if (!TryResolve(command.Order, out var clientOrderId, out var snapshot))
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching simulated order exists.");

        var replacementInstruction = snapshot!.Instruction with { Terms = command.ReplacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(
            Session,
            Capabilities,
            replacementInstruction,
            UtcNow(),
            isReplace: true);
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The simulated account command rate limit was exceeded.");

        var venueResult = _venue.Replace(clientOrderId, command.ReplacementTerms, command.CausationId);
        var scheduled = ScheduleVenueEvents(venueResult.Events, replacementInstruction);
        if (venueResult.Events.Count == 0 && venueResult.EffectiveStatus == VenueCommandStatus.Rejected)
            return Rejected(MapFault(venueResult.Fault), $"The simulated venue rejected replace: {venueResult.Fault}.");

        var receipt = CreateReceipt(BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId);
        return Dispatched(receipt, scheduled);
    }

    /// <inheritdoc />
    public BrokerOrderQueryResult Query(BrokerOrderQuery query)
    {
        if (!query.IsValid)
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.InvalidCommand, null, "The order query is invalid.");
        if (!TryResolveClientOrderId(query, out var clientOrderId))
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.OrderNotFound, null);

        var result = _venue.Query(clientOrderId);
        if (!result.Found || result.Order is null)
            return new BrokerOrderQueryResult(false, MapFault(result.Fault), null, result.Fault.ToString());
        lock (_gate)
            RememberBrokerId(result.Order);
        return new BrokerOrderQueryResult(true, BrokerAdapterCommandFault.None, result.Order);
    }

    /// <inheritdoc />
    public BrokerReconciliationSnapshot CaptureReconciliationSnapshot()
    {
        lock (_gate)
        {
            if (_reconciliationSnapshotOverride is not null)
                return CopySnapshot(_reconciliationSnapshotOverride);
        }

        ClientOrderId[] clientOrderIds;
        lock (_gate)
        {
            clientOrderIds = _knownClientOrderIds
                .OrderBy(item => item.Value, StringComparer.Ordinal)
                .ToArray();
        }

        var open = new List<VenueOrderSnapshot>();
        var completed = new List<VenueOrderSnapshot>();
        var positions = new Dictionary<InstrumentId, long>();
        foreach (var clientOrderId in clientOrderIds)
        {
            var result = _venue.Query(clientOrderId);
            if (!result.Found || result.Order is null)
                continue;
            if (OrderLifecycle.IsTerminal(result.Order.State))
                completed.Add(result.Order);
            else
                open.Add(result.Order);

            if (!result.Order.FilledQuantity.TryGetWholeUnits(out var filledQuantity))
                continue;
            positions.TryGetValue(result.Order.Instruction.TradeIntent.Instrument, out var current);
            positions[result.Order.Instruction.TradeIntent.Instrument] = checked(
                current + (result.Order.Instruction.Terms.Side == OrderSide.Buy
                    ? filledQuantity
                    : -filledQuantity));
        }

        var capturedAtUtc = UtcNow();
        var positionSnapshots = positions
            .OrderBy(item => item.Key.ToString(), StringComparer.Ordinal)
            .Select(item => new BrokerPositionSnapshot(
                item.Key,
                ScaledQuantity.FromWhole(item.Value),
                capturedAtUtc))
            .ToArray();
        BrokerCashSnapshot[] cashSnapshots;
        lock (_gate)
        {
            cashSnapshots = _cash
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new BrokerCashSnapshot(
                    item.Key,
                    item.Value.Total,
                    item.Value.Available,
                    capturedAtUtc))
                .ToArray();
        }
        return new BrokerReconciliationSnapshot(
            Account,
            capturedAtUtc,
            new ReadOnlyCollection<VenueOrderSnapshot>(open),
            new ReadOnlyCollection<VenueOrderSnapshot>(completed),
            Array.AsReadOnly(positionSnapshots),
            Array.AsReadOnly(cashSnapshots));
    }

    /// <summary>
    /// Injects one structurally valid, simulation-only snapshot used by later reconciliation reads.
    /// This changes no venue order and cannot dispatch any command.
    /// </summary>
    public void InjectReconciliationSnapshot(BrokerReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Account != Account ||
            snapshot.CapturedAtUtc.Kind != DateTimeKind.Utc ||
            snapshot.OpenOrders is null || snapshot.CompletedOrders is null ||
            snapshot.Positions is null || snapshot.Cash is null)
        {
            throw new ArgumentException("The injected reconciliation snapshot envelope is invalid.", nameof(snapshot));
        }
        lock (_gate)
            _reconciliationSnapshotOverride = CopySnapshot(snapshot);
    }

    /// <summary>Clears the simulation-only snapshot injection and resumes venue-derived snapshots.</summary>
    public void ClearReconciliationSnapshotInjection()
    {
        lock (_gate)
            _reconciliationSnapshotOverride = null;
    }

    private int ScheduleVenueEvents(
        IReadOnlyList<VenueEvent> venueEvents,
        CanonicalOrderInstruction instruction)
    {
        var scheduled = 0;
        foreach (var rawEvent in venueEvents)
        {
            var venueEvent = ScopeEvent(rawEvent);
            lock (_gate)
                RememberBrokerId(venueEvent);

            if (venueEvent.Kind == VenueEventKind.Fill)
            {
                AccountFillCash(instruction.Terms.Side, venueEvent);
                var execution = new BrokerExecutionEvent(
                    EventId(venueEvent.DeduplicationKey, "execution"),
                    Account,
                    venueEvent.ClientOrderId,
                    venueEvent.OccurredAtUtc,
                    venueEvent);
                Schedule(() => Publish(execution));
                scheduled++;
                if (_duplicateCallbacks)
                {
                    Schedule(() => Publish(execution));
                    scheduled++;
                }

                if (venueEvent.Fill is { } fill)
                {
                    var commission = new BrokerCommissionEvent(
                        EventId(venueEvent.DeduplicationKey, "commission"),
                        Account,
                        venueEvent.ClientOrderId,
                        venueEvent.OccurredAtUtc,
                        venueEvent.CausationId,
                        fill.Fee);
                    Schedule(() => Publish(commission));
                    scheduled++;
                    Schedule(() => PublishPosition(instruction, venueEvent, fill));
                    scheduled++;
                }
            }
            else
            {
                var order = new BrokerOrderEvent(
                    EventId(venueEvent.DeduplicationKey, "order"),
                    Account,
                    venueEvent.ClientOrderId,
                    venueEvent.OccurredAtUtc,
                    venueEvent);
                Schedule(() => Publish(order));
                scheduled++;
                if (_duplicateCallbacks)
                {
                    Schedule(() => Publish(order));
                    scheduled++;
                }
            }
        }

        return scheduled;
    }

    private void PublishPosition(
        CanonicalOrderInstruction instruction,
        VenueEvent venueEvent,
        in FillExecution fill)
    {
        if (!fill.Quantity.TryGetWholeUnits(out var fillQuantity))
            return;

        long position;
        lock (_gate)
        {
            _positions.TryGetValue(instruction.TradeIntent.Instrument, out var current);
            try
            {
                position = checked(current +
                    (instruction.Terms.Side == OrderSide.Buy ? fillQuantity : -fillQuantity));
            }
            catch (OverflowException)
            {
                return;
            }
            _positions[instruction.TradeIntent.Instrument] = position;
        }

        Publish(new BrokerPositionEvent(
            EventId(venueEvent.DeduplicationKey, "position"),
            Account,
            venueEvent.ClientOrderId,
            venueEvent.OccurredAtUtc,
            venueEvent.CausationId,
            instruction.TradeIntent.Instrument,
            ScaledQuantity.FromWhole(position)));
    }

    private void AccountFillCash(OrderSide side, VenueEvent venueEvent)
    {
        if (venueEvent.Fill is not { } fill)
            return;
        lock (_gate)
        {
            if (!_accountedFillKeys.Add(venueEvent.DeduplicationKey) ||
                !_cash.TryGetValue(_settlementCurrency, out var balance) ||
                !TryApplyCashDelta(balance.Total, side, fill, out var total) ||
                !TryApplyCashDelta(balance.Available, side, fill, out var available))
            {
                return;
            }
            _cash[_settlementCurrency] = new SimulatedCashBalance(total, available);
        }
    }

    private static bool TryApplyCashDelta(
        ScaledMoney opening,
        OrderSide side,
        in FillExecution fill,
        out ScaledMoney result)
    {
        result = default;
        if (!ScaledValueMath.TryMultiply(
                fill.Quantity.Coefficient,
                fill.Price.Coefficient,
                out var notional))
        {
            return false;
        }
        if (side == OrderSide.Buy)
            notional = -notional;
        if (!ScaledValueMath.TryAdd(
                notional,
                fill.Quantity.Scale + fill.Price.Scale,
                -(Int128)fill.Fee.Coefficient,
                fill.Fee.Scale,
                out var delta,
                out var deltaScale) ||
            !ScaledValueMath.TryAdd(
                opening.Coefficient,
                opening.Scale,
                delta,
                deltaScale,
                out var final,
                out var finalScale) ||
            !ScaledValueMath.TryNarrow(final, finalScale, out var coefficient, out var scale))
        {
            return false;
        }
        result = new ScaledMoney(coefficient, scale);
        return true;
    }

    private VenueEvent ScopeEvent(VenueEvent venueEvent)
    {
        var material = $"{Account.AdapterId.Value}|{Account.AccountId.Value}|{venueEvent.DeduplicationKey.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return venueEvent with
        {
            DeduplicationKey = new DeduplicationKey($"adapter:{Convert.ToHexString(hash).ToLowerInvariant()}"),
        };
    }

    private BrokerDispatchReceipt CreateReceipt(
        BrokerAdapterCommandKind commandKind,
        ClientOrderId clientOrderId,
        CausationId causationId)
    {
        var material = $"{Account.AdapterId.Value}|{Account.AccountId.Value}|{commandKind}|{clientOrderId.Value}|{causationId.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerDispatchReceipt(
            new DispatchReceiptId($"sim-dispatch-{Convert.ToHexString(hash).ToLowerInvariant()}"),
            Account,
            commandKind,
            clientOrderId,
            causationId,
            UtcNow());
    }

    private bool TryResolve(
        in BrokerOrderQuery query,
        out ClientOrderId clientOrderId,
        out VenueOrderSnapshot? snapshot)
    {
        snapshot = null;
        if (!TryResolveClientOrderId(query, out clientOrderId))
            return false;
        var result = _venue.Query(clientOrderId);
        snapshot = result.Order;
        return result.Found && snapshot is not null;
    }

    private bool TryResolveClientOrderId(in BrokerOrderQuery query, out ClientOrderId clientOrderId)
    {
        if (query.ClientOrderId.HasValue)
        {
            clientOrderId = query.ClientOrderId.Value;
            return true;
        }

        lock (_gate)
        {
            if (query.BrokerOrderId.HasValue &&
                _brokerToClient.TryGetValue(query.BrokerOrderId.Value, out clientOrderId))
            {
                return true;
            }
        }

        clientOrderId = default;
        return false;
    }

    private bool TryConsumeRateBudget()
    {
        lock (_gate)
        {
            var now = UtcNow();
            if (now < _rateWindowStartedUtc || now - _rateWindowStartedUtc >= Capabilities.RateLimit.Window)
            {
                _rateWindowStartedUtc = now;
                _commandsInRateWindow = 0;
            }
            if (_commandsInRateWindow >= Capabilities.RateLimit.MaximumCommands)
                return false;
            _commandsInRateWindow++;
            return true;
        }
    }

    private void RememberBrokerId(VenueOrderSnapshot? snapshot)
    {
        if (snapshot?.BrokerOrderId is { } brokerOrderId)
            _brokerToClient[brokerOrderId] = snapshot.Instruction.Identity.ClientOrderId;
    }

    private void RememberBrokerId(VenueEvent venueEvent)
    {
        if (venueEvent.BrokerOrderId is { } brokerOrderId)
            _brokerToClient[brokerOrderId] = venueEvent.ClientOrderId;
    }

    private void Schedule(Action callback) => _scheduler.Schedule(callback);

    private void Publish(BrokerAdapterEvent adapterEvent) => EventReceived?.Invoke(adapterEvent);

    private static BrokerAdapterEventId EventId(DeduplicationKey key, string category) =>
        new($"{key.Value}:{category}");

    private static BrokerAdapterCommandResult Dispatched(
        BrokerDispatchReceipt receipt,
        int scheduledEventCount) =>
        new(
            BrokerAdapterCommandStatus.Dispatched,
            BrokerAdapterCommandFault.None,
            receipt,
            scheduledEventCount,
            null);

    private static BrokerAdapterCommandResult Rejected(BrokerAdapterCommandFault fault, string reason) =>
        new(BrokerAdapterCommandStatus.RejectedBeforeDispatch, fault, null, 0, reason);

    private static BrokerAdapterCommandResult AdmissionRejected(in ExecutionAdmissionResult admission) =>
        Rejected(
            admission.Fault is ExecutionAdmissionFault.DataDisconnected or
                ExecutionAdmissionFault.ExecutionNotAuthenticated or
                ExecutionAdmissionFault.ExecutionNotCertified or
                ExecutionAdmissionFault.SessionUnavailable or
                ExecutionAdmissionFault.InvalidSession
                ? BrokerAdapterCommandFault.ExecutionUnavailable
                : BrokerAdapterCommandFault.UnsupportedCapability,
            admission.Reason ?? admission.Fault.ToString());

    private static BrokerAdapterCommandFault MapFault(VenueCommandFault fault) =>
        fault switch
        {
            VenueCommandFault.OrderNotFound => BrokerAdapterCommandFault.OrderNotFound,
            VenueCommandFault.IdempotencyConflict => BrokerAdapterCommandFault.Conflict,
            VenueCommandFault.UnsupportedCapability or VenueCommandFault.InvalidReplacement =>
                BrokerAdapterCommandFault.UnsupportedCapability,
            VenueCommandFault.OutcomeUnknown => BrokerAdapterCommandFault.OutcomeUnknown,
            _ => BrokerAdapterCommandFault.VenueRejected,
        };

    private DateTime UtcNow()
    {
        var value = _clock.UtcNow;
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static BrokerReconciliationSnapshot CopySnapshot(BrokerReconciliationSnapshot snapshot) =>
        new(
            snapshot.Account,
            snapshot.CapturedAtUtc,
            Array.AsReadOnly(snapshot.OpenOrders.ToArray()),
            Array.AsReadOnly(snapshot.CompletedOrders.ToArray()),
            Array.AsReadOnly(snapshot.Positions.ToArray()),
            Array.AsReadOnly(snapshot.Cash.ToArray()));

    private readonly record struct SimulatedCashBalance(ScaledMoney Total, ScaledMoney Available);
}
