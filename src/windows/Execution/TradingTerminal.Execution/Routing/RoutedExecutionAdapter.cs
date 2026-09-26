using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Routing;

/// <summary>
/// The execution adapter for every routed broker: turns an <see cref="IBrokerOrderRoute"/> into the engine's
/// <see cref="IBrokerExecutionAdapter"/> contract, once, for all of them.
///
/// <para><b>Derived from the Alpaca adapter</b>, whose behaviour it keeps: commands return a local dispatch
/// receipt and never wait on the network; broker answers arrive as events through the scheduler; fills are
/// published as exact deltas of the broker's cumulative quantity; an answer that cannot be represented
/// exactly becomes <c>OutcomeUnknown</c>, which blocks the book until reconciliation, rather than a guess; a
/// live command re-checks the live gate every time and consumes the coordinator's one-use guardrail.</para>
///
/// <para><b>Two roles.</b> Constructed without an instrument, it is a broker <i>connection</i>: it signs in,
/// reads the account and backs the console's broker card, but has no capabilities and refuses every order.
/// <see cref="CreateBookAdapter"/> binds a copy to one instrument for one book — its own lease, its own
/// position — so several books can trade different symbols on one broker account.</para>
///
/// <para><b>Units.</b> The engine trades whole units. The route says what one unit is
/// (<see cref="RouteInstrument.UnitSize"/>): a share, a contract, or an exchange's quantity step for crypto.
/// Every broker quantity is divided by it and must come out whole; one that does not is not representable,
/// and is treated as such.</para>
///
/// <para><b>The book owns what it trades.</b> The position the account already held when the book attached
/// is recorded once as a baseline and subtracted, so a user who already holds shares or coins can still
/// attach a book; a change the book did not make (a manual trade in the same symbol) still shows as a
/// mismatch and stops the book.</para>
/// </summary>
public sealed class RoutedExecutionAdapter : IBookableExecutionAdapter, IDisposable, IAsyncDisposable
{
    private const int MaximumPendingOperations = 512;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly IBrokerOrderRoute _route;
    private readonly RoutedExecutionOptions _options;
    private readonly ILiveExecutionConfirmationStore? _confirmationStore;
    private readonly IClock _clock;
    private readonly IAdapterEventScheduler _scheduler;
    private readonly SerializedAdapterEventScheduler? _ownedScheduler;
    private readonly bool _hasCredentials;
    private readonly string _expectedAccountId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ClientOrderId, TrackedOrder> _orders = [];
    private readonly Dictionary<BrokerOrderId, ClientOrderId> _brokerToClient = [];
    private readonly Queue<ClientOrderId> _insertionOrder = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private CancellationTokenSource? _connectionLifetime;
    private Task? _pollLoop;
    private BrokerExecutionSession _session;
    private BrokerExecutionCapabilities _capabilities;
    private BrokerReconciliationSnapshot _snapshot;
    private RouteInstrument? _rules;
    private decimal _baselineNative;
    private bool _baselineCaptured;
    private DateTime _rateWindowStartedUtc;
    private int _commandsInRateWindow;
    private int _pendingOperationSlots;
    private int _pollFailures;
    private bool _disposed;

    /// <summary>A broker connection (no instrument). Throws before anything is sent when the environment is
    /// not permitted: LIVE without every condition of the live gate, or PAPER on a broker that has none.</summary>
    public RoutedExecutionAdapter(
        IBrokerOrderRoute route,
        ExecutionMode mode,
        RoutedExecutionOptions options,
        bool hasCredentials,
        IClock clock,
        IAdapterEventScheduler? scheduler = null,
        ILiveExecutionConfirmationStore? confirmationStore = null,
        string expectedAccountId = "")
        : this(route, mode, options, hasCredentials, clock, scheduler, confirmationStore, expectedAccountId, null, null, null)
    {
    }

    private RoutedExecutionAdapter(
        IBrokerOrderRoute route,
        ExecutionMode mode,
        RoutedExecutionOptions options,
        bool hasCredentials,
        IClock clock,
        IAdapterEventScheduler? scheduler,
        ILiveExecutionConfirmationStore? confirmationStore,
        string expectedAccountId,
        InstrumentId? instrument,
        string? symbol,
        string? boundAccountId)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        var fault = _options.Validate();
        if (fault is not null)
            throw new InvalidOperationException(fault);
        if (!Enum.IsDefined(mode))
            throw new InvalidOperationException("The execution mode is invalid.");
        if (string.IsNullOrWhiteSpace(route.RouteId) || route.RouteId.Length > LiveExecutionConfirmation.MaximumBrokerIdLength)
            throw new InvalidOperationException("The order route has no bounded stable identity.");

        Mode = mode;
        _hasCredentials = hasCredentials;
        _expectedAccountId = expectedAccountId?.Trim() ?? string.Empty;
        _confirmationStore = confirmationStore;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // The gate, before a single request can exist: exactly the conditions Alpaca, cTrader and IB
        // enforce for LIVE, and a refusal for PAPER on a broker without a paper environment.
        if (mode == ExecutionMode.Live)
        {
            _ = LiveExecutionAuthorizationGate.Require(
                _options.AllowLiveExecution, hasCredentials, route.RouteId, _expectedAccountId, confirmationStore);
        }
        else if (route.PaperEnvironmentName is null)
        {
            throw new InvalidOperationException(
                $"{route.DisplayName} has no paper or demo environment; it can only be traded LIVE, behind the live gate.");
        }

        if (scheduler is null)
        {
            _ownedScheduler = new SerializedAdapterEventScheduler();
            _scheduler = _ownedScheduler;
        }
        else
        {
            _scheduler = scheduler;
            _ownedScheduler = scheduler as SerializedAdapterEventScheduler;
        }

        IsBound = instrument is not null;
        Instrument = instrument ?? default;
        Symbol = symbol ?? string.Empty;
        var accountId = boundAccountId is not null
            ? $"{boundAccountId}/{Symbol}"
            : _expectedAccountId.Length > 0 ? _expectedAccountId : $"{AdapterId}-account";
        Account = new BrokerExecutionAccount(new ExecutionAdapterId(AdapterId), new BrokerAccountId(accountId));
        NativeAccountId = boundAccountId;
        var now = UtcNow();
        _session = new BrokerExecutionSession(Account, ExecutionSessionHealth.Disconnected, false, false, false, now);
        _capabilities = UnavailableCapabilities();
        _snapshot = EmptySnapshot(DateTime.UnixEpoch);
        _rateWindowStartedUtc = now;
        if (_ownedScheduler is not null)
            _ownedScheduler.CallbackFaulted += OnSchedulerFaulted;
    }

    // ── Identity ─────────────────────────────────────────────────────────────────────────────────

    public string BrokerId => _route.RouteId;

    public string DisplayName => _route.DisplayName;

    public IBrokerOrderRoute Route => _route;

    public ExecutionMode Mode { get; }

    public RouteEnvironment Environment => Mode == ExecutionMode.Live ? RouteEnvironment.Live : RouteEnvironment.Paper;

    /// <summary>What the console shows for the environment: LIVE, or the broker's own name for its paper one.</summary>
    public string EnvironmentLabel => Mode == ExecutionMode.Live ? "LIVE" : _route.PaperEnvironmentName ?? "PAPER";

    public string AdapterId => $"{_route.RouteId}-{(Mode == ExecutionMode.Live ? "live" : "paper")}";

    /// <summary>True for a book adapter bound to one instrument; false for a broker connection.</summary>
    public bool IsBound { get; }

    public InstrumentId Instrument { get; }

    public string Symbol { get; }

    /// <summary>The account the broker says the credentials reach, once connected.</summary>
    public string? NativeAccountId { get; private set; }

    /// <summary>The account a LIVE adapter was confirmed for; empty for PAPER.</summary>
    public string ExpectedAccountId => _expectedAccountId;

    public string? AccountCurrency { get; private set; }

    public RouteInstrument? Rules
    {
        get
        {
            lock (_gate)
                return _rules;
        }
    }

    /// <summary>The position the account held when the book attached, in the broker's own quantity.</summary>
    public decimal BaselinePosition
    {
        get
        {
            lock (_gate)
                return _baselineNative;
        }
    }

    public ScaledRatio ContractMultiplier
    {
        get
        {
            lock (_gate)
            {
                return _rules is { } rules && RouteValues.TryRatio(rules.ValuePerPoint, out var ratio)
                    ? ratio
                    : new ScaledRatio(1, 0);
            }
        }
    }

    public ScaledPrice? LatestReferencePrice { get; private set; }

    public DateTime? LatestReferencePriceObservedAtUtc { get; private set; }

    public DateTime? LatestReferencePriceFetchedAtUtc { get; private set; }

    public BrokerExecutionAccount Account { get; }

    public BrokerExecutionSession Session
    {
        get
        {
            lock (_gate)
                return _session;
        }
    }

    public BrokerExecutionCapabilities Capabilities
    {
        get
        {
            lock (_gate)
                return _capabilities;
        }
    }

    public event Action<BrokerAdapterEvent>? EventReceived;

    /// <summary>
    /// Where a reference price comes from when the broker gives none (Tradovate, tastytrade and IB have no price
    /// call here): the terminal's own live market data for the book's instrument. Copied into every book adapter
    /// this card creates. The same freshness rule applies — a price older than the engine allows is refused.
    /// </summary>
    public Func<InstrumentId, RoutePrice?>? ReferencePriceFallback { get; set; }

    /// <summary>
    /// A copy of this connection bound to one instrument, for one book. Not connected: the caller connects
    /// it, which reads the instrument's rules and records the baseline position.
    /// </summary>
    public RoutedExecutionAdapter CreateBookAdapter(InstrumentId instrument, string symbol, IAdapterEventScheduler? scheduler = null)
    {
        ThrowIfDisposed();
        if (IsBound)
            throw new InvalidOperationException("A book adapter cannot be bound again.");
        if (instrument.IsNone || string.IsNullOrWhiteSpace(symbol) || symbol.Length > 64)
            throw new ArgumentException("A book adapter needs one resolved instrument and its broker symbol.");
        if (NativeAccountId is not { } account || !Session.IsExecutionAuthenticated)
            throw new InvalidOperationException($"Connect {DisplayName} before attaching a book.");
        return new RoutedExecutionAdapter(
            _route, Mode, _options, _hasCredentials, _clock, scheduler, _confirmationStore, _expectedAccountId,
            instrument, symbol.Trim(), account)
        {
            ReferencePriceFallback = ReferencePriceFallback,
        };
    }

    // ── Connection ───────────────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateLiveAuthorization(out var authorizationFault))
        {
            CloseUnauthorizedSession();
            throw new InvalidOperationException(authorizationFault);
        }

        var priorAccount = NativeAccountId;
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource connectionLifetime;
        lock (_gate)
        {
            _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectionLifetime = _connectionLifetime;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionLifetime.Token);
        var token = attempt.Token;
        try
        {
            var account = await _route.ConnectAsync(Environment, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(account.AccountId) || account.AccountId.Length > LiveExecutionConfirmation.MaximumAccountIdLength)
                throw new InvalidDataException($"{DisplayName} returned no usable account identifier.");
            if (Mode == ExecutionMode.Live && !string.Equals(account.AccountId, _expectedAccountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The {DisplayName} LIVE credentials reach account '{account.AccountId}', not the confirmed '{_expectedAccountId}'.");
            }
            if (priorAccount is not null && !string.Equals(priorAccount, account.AccountId, StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    if (_orders.Count != 0 || IsBound)
                        throw new InvalidOperationException($"The {DisplayName} account changed while this binding was in use; attach a new book.");
                }
            }

            NativeAccountId = account.AccountId;
            AccountCurrency = account.Currency;
            SetSession(ExecutionSessionHealth.Degraded, true, true, false);

            if (IsBound)
            {
                var rules = await _route.InstrumentAsync(Environment, Symbol, token).ConfigureAwait(false);
                if (!rules.IsValid || !string.Equals(rules.Symbol, Symbol, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{DisplayName} returned unusable rules for {Symbol}.");
                var capabilities = CapabilitiesFor(rules)
                    ?? throw new InvalidDataException($"The {DisplayName} rules for {Symbol} cannot be represented exactly.");
                lock (_gate)
                {
                    _rules = rules;
                    _capabilities = capabilities;
                }

                if (!_baselineCaptured)
                {
                    var position = await _route.PositionAsync(Environment, Symbol, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _baselineNative = position.Quantity;
                        _baselineCaptured = true;
                    }
                }

                await RefreshReconciliationCoreAsync(token).ConfigureAwait(false);
                try
                {
                    await RefreshReferencePriceAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ClearReferencePrice();
                }

                _pollLoop = Task.Run(() => PollLoopAsync(connectionLifetime.Token), CancellationToken.None);
            }

            SetSession(ExecutionSessionHealth.Healthy, true, true, true);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_connectionLifetime, connectionLifetime))
                    _connectionLifetime = null;
            }
            connectionLifetime.Cancel();
            connectionLifetime.Dispose();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
            NativeAccountId = IsBound ? NativeAccountId : priorAccount;
            ClearReferencePrice();
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        CancellationTokenSource? connectionLifetime;
        Task? pollLoop;
        lock (_gate)
        {
            connectionLifetime = _connectionLifetime;
            _connectionLifetime = null;
            pollLoop = _pollLoop;
            _pollLoop = null;
        }
        connectionLifetime?.Cancel();
        try
        {
            if (pollLoop is not null)
            {
                try { await pollLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            connectionLifetime?.Dispose();
            ClearReferencePrice();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    public async Task RefreshReferencePriceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBound)
            throw new InvalidOperationException("A broker connection has no instrument to price.");
        ClearReferencePrice();
        var price = await _route.PriceAsync(Environment, Symbol, cancellationToken).ConfigureAwait(false)
            ?? ReferencePriceFallback?.Invoke(Instrument);
        if (price is null || price.ObservedAtUtc.Kind != DateTimeKind.Utc || !RouteValues.TryPrice(price.Price, out var exact))
            return;
        LatestReferencePrice = exact;
        LatestReferencePriceObservedAtUtc = price.ObservedAtUtc;
        LatestReferencePriceFetchedAtUtc = UtcNow();
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command)
    {
        if (command is null || command.Instruction is null || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, $"The {DisplayName} submit command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live && !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"{DisplayName} LIVE submit requires a current one-use OMS guardrail admission.");
        if (!IsBound || command.Instruction.TradeIntent.Instrument != Instrument)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, $"This {DisplayName} adapter is bound to {Symbol} only.");

        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, command.Instruction, UtcNow());
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, $"The local {DisplayName} command budget is exhausted.");
        if (!TryMapRequest(command.Instruction, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);

        var clientOrderId = command.Instruction.Identity.ClientOrderId;
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The {DisplayName} connection has ended.");
        lock (_gate)
        {
            if (_orders.ContainsKey(clientOrderId))
            {
                return new BrokerAdapterCommandResult(
                    BrokerAdapterCommandStatus.Conflict, BrokerAdapterCommandFault.Conflict, null, 0,
                    $"The client order ID is already bound to a {DisplayName} order.");
            }
            if (!MakeOrderCapacity())
                return Rejected(BrokerAdapterCommandFault.RateLimited, $"The bounded {DisplayName} order table is full.");
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, $"The bounded {DisplayName} operation table is full.");
            _pendingOperationSlots++;
            _orders.Add(clientOrderId, new TrackedOrder(command.Instruction, command.Instruction.Terms, command.CausationId));
            _insertionOrder.Enqueue(clientOrderId);
        }

        TrackOperation(SubmitAsync(clientOrderId, request!, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Submit, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid)
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, $"The {DisplayName} cancel command is invalid.");
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live && !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"{DisplayName} LIVE cancel requires a current one-use OMS guardrail admission.");
        if (!Session.CanExecute)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The {DisplayName} session cannot execute.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) || tracked!.BrokerOrderId is null)
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, $"No matching {DisplayName} order is known.");
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, $"The local {DisplayName} command budget is exhausted.");
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The {DisplayName} connection has ended.");

        RouteOrder target;
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, $"The bounded {DisplayName} operation table is full.");
            _pendingOperationSlots++;
            tracked.State = OrderLifecycleState.PendingCancel;
            tracked.CausationId = command.CausationId;
            target = RouteOrderOf(tracked);
        }
        TrackOperation(CancelAsync(clientOrderId, target, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Cancel, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, $"The {DisplayName} replace command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live && !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"{DisplayName} LIVE replace requires a current one-use OMS guardrail admission.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) || tracked!.BrokerOrderId is null)
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, $"No matching {DisplayName} order is known.");
        if (command.ReplacementTerms.Side != tracked.CurrentTerms.Side ||
            command.ReplacementTerms.OrderType != tracked.CurrentTerms.OrderType ||
            command.ReplacementTerms.TimeInForce != tracked.CurrentTerms.TimeInForce)
        {
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, $"{DisplayName} replacement cannot change side, order type or time in force.");
        }

        var replacement = tracked.Instruction with { Terms = command.ReplacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, replacement, UtcNow(), isReplace: true);
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, $"The local {DisplayName} command budget is exhausted.");
        if (!TryNative(command.ReplacementTerms.Quantity, out var quantity))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "The replacement quantity has no exact broker representation.");
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The {DisplayName} connection has ended.");

        RouteOrder target;
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, $"The bounded {DisplayName} operation table is full.");
            _pendingOperationSlots++;
            tracked.State = OrderLifecycleState.PendingReplace;
            tracked.CausationId = command.CausationId;
            tracked.PendingReplacement = command.ReplacementTerms;
            target = RouteOrderOf(tracked);
        }
        TrackOperation(ReplaceAsync(
            clientOrderId, target, quantity,
            RouteValues.ToDecimal(command.ReplacementTerms.LimitPrice), RouteValues.ToDecimal(command.ReplacementTerms.StopPrice),
            command.ReplacementTerms, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId));
    }

    public BrokerOrderQueryResult Query(BrokerOrderQuery query)
    {
        // Cache-only, as the seam requires: the poll loop and RefreshReconciliationAsync keep it current.
        if (!query.IsValid)
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.InvalidCommand, null, $"The {DisplayName} query is invalid.");
        if (!TryResolve(query, out _, out var tracked))
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.OrderNotFound, null);
        lock (_gate)
            return new BrokerOrderQueryResult(true, BrokerAdapterCommandFault.None, ToVenueSnapshot(tracked!));
    }

    public BrokerReconciliationSnapshot CaptureReconciliationSnapshot()
    {
        lock (_gate)
            return CopySnapshot(_snapshot);
    }

    public async Task RefreshReconciliationAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBound)
            throw new InvalidOperationException("A broker connection has no book to reconcile.");
        if (!TryGetConnectionToken(out var connectionToken))
            throw new InvalidOperationException($"The {DisplayName} connection has ended.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionToken);
        await RefreshReconciliationCoreAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads orders, the position and cash, applies every order change through the ordinary event path, and
    /// waits for those events to reach the engine before capturing the snapshot — so the reconciliation that
    /// follows compares the ledger with a broker state the ledger has already been told about.
    /// </summary>
    private async Task RefreshReconciliationCoreAsync(CancellationToken cancellationToken)
    {
        await PollOrdersAsync(includeAll: true, cancellationToken).ConfigureAwait(false);
        var positionTask = _route.PositionAsync(Environment, Symbol, cancellationToken);
        var accountTask = _route.AccountAsync(Environment, cancellationToken);
        await Task.WhenAll(positionTask, accountTask).ConfigureAwait(false);
        var position = await positionTask.ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        if (!string.Equals(account.AccountId, NativeAccountId, StringComparison.Ordinal))
            throw new InvalidDataException($"The {DisplayName} reconciliation response belonged to another account.");
        await DrainSchedulerAsync(cancellationToken).ConfigureAwait(false);

        var now = UtcNow();
        lock (_gate)
        {
            var positions = BookPosition(position.Quantity) is { } units
                ? new[] { new BrokerPositionSnapshot(Instrument, units, now) }
                : [];
            var cash = RouteValues.TryMoney(account.CashTotal, out var total) && RouteValues.TryMoney(account.CashAvailable, out var available)
                ? new[] { new BrokerCashSnapshot(account.Currency, total, available, now) }
                : [];
            var tracked = _orders.Values.Where(order => order.BrokerOrderId is not null || order.Acknowledged).Select(ToVenueSnapshot).ToArray();
            _snapshot = new BrokerReconciliationSnapshot(
                Account,
                now,
                Array.AsReadOnly(tracked.Where(order => !OrderLifecycle.IsTerminal(order.State)).ToArray()),
                Array.AsReadOnly(tracked.Where(order => OrderLifecycle.IsTerminal(order.State)).ToArray()),
                Array.AsReadOnly(positions),
                Array.AsReadOnly(cash));
        }
    }

    /// <summary>
    /// The book's position in engine units: the broker's position less the baseline, plus fees the exchange
    /// took in the base asset (they reduced the balance without being a trade). Null when the quantity is not
    /// representable — which the reconciliation then reports as a missing position, and stops the book.
    /// </summary>
    private ScaledQuantity? BookPosition(decimal native)
    {
        if (_rules is not { } rules)
            return null;
        var baseFees = _orders.Values.Sum(order => order.BaseAssetFee);
        var bookNative = native - _baselineNative + baseFees;
        return RouteValues.TryUnitsExact(bookNative, rules.UnitSize, out var whole)
            ? ScaledQuantity.FromWhole(whole)
            : RouteValues.TryQuantity(bookNative / rules.UnitSize, out var fractional) ? fractional : null;
    }

    // ── Broker answers ───────────────────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                bool active;
                lock (_gate)
                    active = _orders.Values.Any(order => !OrderLifecycle.IsTerminal(order.State));
                if (!active)
                    continue;
                await PollOrdersAsync(includeAll: false, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _pollFailures, 0) >= 3)
                    SetSession(ExecutionSessionHealth.Healthy, true, true, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Three misses in a row stop new orders until the broker answers again; one is weather.
                if (Interlocked.Increment(ref _pollFailures) == 3)
                    SetSession(ExecutionSessionHealth.Degraded, true, Session.IsExecutionAuthenticated, false);
            }
        }
    }

    /// <summary>Reads the open-orders list, then asks individually about every tracked order it does not
    /// contain — a broker that lists only open orders drops a filled one from the list the moment it fills.</summary>
    private async Task PollOrdersAsync(bool includeAll, CancellationToken cancellationToken)
    {
        var listed = await _route.OrdersAsync(Environment, Symbol, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in listed)
        {
            if (!string.Equals(order.Symbol, Symbol, StringComparison.OrdinalIgnoreCase))
                continue;
            if (OnOrderUpdated(order))
                seen.Add(order.OrderId);
        }

        (string OrderId, string ClientOrderId)[] missing;
        lock (_gate)
        {
            missing = _orders.Values
                .Where(order => (includeAll || !OrderLifecycle.IsTerminal(order.State)) &&
                                order.BrokerOrderId is { } id && !seen.Contains(id.Value) &&
                                !OrderLifecycle.IsTerminal(order.State))
                .Select(order => (order.BrokerOrderId!.Value.Value, order.Instruction.Identity.ClientOrderId.Value))
                .ToArray();
        }

        foreach (var (orderId, clientOrderId) in missing)
        {
            var order = await _route.OrderAsync(Environment, Symbol, orderId, clientOrderId, cancellationToken).ConfigureAwait(false);
            if (order is not null)
                OnOrderUpdated(order);
        }
    }

    /// <summary>Applies one broker order state. Returns true when it belonged to this adapter.</summary>
    private bool OnOrderUpdated(RouteOrder update)
    {
        if (_disposed || _rules is null)
            return false;

        TrackedOrder? tracked;
        lock (_gate)
        {
            tracked = null;
            if (!string.IsNullOrEmpty(update.ClientOrderId))
                _orders.TryGetValue(new ClientOrderId(update.ClientOrderId), out tracked);
            if (tracked is null && !string.IsNullOrEmpty(update.OrderId) &&
                _brokerToClient.TryGetValue(new BrokerOrderId(update.OrderId), out var client))
            {
                _orders.TryGetValue(client, out tracked);
            }
            if (tracked is null)
                return false;
            if (!string.IsNullOrEmpty(update.OrderId))
                RememberBrokerId(tracked, update.OrderId);
        }

        switch (update.Status)
        {
            case RouteOrderStatus.PendingNew:
            case RouteOrderStatus.PendingCancel:
            case RouteOrderStatus.Unknown:
                return true;

            case RouteOrderStatus.Working:
                Acknowledge(tracked, update.UpdatedAtUtc);
                return true;

            case RouteOrderStatus.PartiallyFilled:
            case RouteOrderStatus.Filled:
                Acknowledge(tracked, update.UpdatedAtUtc);
                PublishFillDelta(tracked, update);
                return true;

            default:
                // A cancelled or expired order can carry fills from before it ended; they come first.
                if (update.FilledQuantity > 0)
                {
                    Acknowledge(tracked, update.UpdatedAtUtc);
                    PublishFillDelta(tracked, update);
                }
                var kind = update.Status switch
                {
                    RouteOrderStatus.Cancelled => VenueEventKind.Cancelled,
                    RouteOrderStatus.Expired => VenueEventKind.Expired,
                    _ => VenueEventKind.Rejected,
                };
                bool alreadyTerminal;
                lock (_gate)
                    alreadyTerminal = OrderLifecycle.IsTerminal(tracked.State);
                if (!alreadyTerminal)
                    PublishTerminal(tracked.Instruction.Identity.ClientOrderId, kind, update.Reason, update.UpdatedAtUtc);
                return true;
        }
    }

    private void Acknowledge(TrackedOrder tracked, DateTime occurredAtUtc)
    {
        var publish = false;
        lock (_gate)
        {
            if (!tracked.Acknowledged && !OrderLifecycle.IsTerminal(tracked.State))
            {
                tracked.Acknowledged = true;
                if (tracked.State is OrderLifecycleState.Acknowledging)
                    tracked.State = OrderLifecycleState.Working;
                publish = true;
            }
        }
        if (publish)
            PublishOrderEvent(tracked, VenueEventKind.Acknowledged, occurredAtUtc);
    }

    private void PublishFillDelta(TrackedOrder tracked, RouteOrder update)
    {
        FillExecution fill;
        BrokerPositionEvent positionEvent;
        var clientOrderId = tracked.Instruction.Identity.ClientOrderId;
        lock (_gate)
        {
            var rules = _rules!;
            if (update.FilledQuantity <= tracked.FilledNative)
                return;
            if (!RouteValues.TryUnitsExact(update.FilledQuantity, rules.UnitSize, out var cumulativeUnits) ||
                update.AveragePrice is not { } average || average <= 0 ||
                !RouteValues.TryIncrementalPrice(tracked.FilledNative, tracked.AveragePrice, update.FilledQuantity, average, out var deltaPrice) ||
                !RouteValues.TryPrice(deltaPrice, out var price))
            {
                tracked.State = OrderLifecycleState.Unknown;
                PublishOrderEvent(tracked, VenueEventKind.OutcomeUnknown, update.UpdatedAtUtc,
                    reason: $"The {DisplayName} fill could not be represented exactly; reconcile before trading on.");
                return;
            }

            var deltaUnits = cumulativeUnits - tracked.FilledUnits;
            if (deltaUnits <= 0)
                return;

            // The fee delta counts only when it is in the account's price currency; a fee in the base asset is
            // remembered for the position instead, and one in a third asset (BNB) is not cash in this ledger.
            var feeDelta = 0m;
            if (string.Equals(update.FeeCurrency, rules.Currency, StringComparison.OrdinalIgnoreCase))
                feeDelta = Math.Max(0m, update.Fee - tracked.QuoteFee);
            if (rules.BaseAsset.Length > 0 && string.Equals(update.FeeCurrency, rules.BaseAsset, StringComparison.OrdinalIgnoreCase))
                tracked.BaseAssetFee = update.Fee;
            else if (string.Equals(update.FeeCurrency, rules.Currency, StringComparison.OrdinalIgnoreCase))
                tracked.QuoteFee = update.Fee;
            if (!RouteValues.TryMoney(decimal.Round(feeDelta, 12), out var fee))
                fee = ScaledMoney.Zero;

            tracked.FilledNative = update.FilledQuantity;
            tracked.FilledUnits = cumulativeUnits;
            tracked.AveragePrice = average;
            tracked.State = update.Status == RouteOrderStatus.Filled || cumulativeUnits >= RouteValues.Units(tracked.CurrentTerms.Quantity)
                ? OrderLifecycleState.Filled
                : OrderLifecycleState.PartiallyFilled;
            fill = new FillExecution(ScaledQuantity.FromWhole(deltaUnits), price, fee, LiquidityFlag.Taker);

            positionEvent = new BrokerPositionEvent(
                EventId(tracked, "position", update.UpdatedAtUtc, cumulativeUnits),
                Account,
                clientOrderId,
                Utc(update.UpdatedAtUtc),
                tracked.CausationId,
                Instrument,
                InferredPosition());
        }

        var venueEvent = VenueEvent(tracked, VenueEventKind.Fill, update.UpdatedAtUtc, fill: fill);
        var execution = new BrokerExecutionEvent(
            EventId(tracked, "execution", update.UpdatedAtUtc, tracked.FilledUnits), Account, clientOrderId, Utc(update.UpdatedAtUtc), venueEvent);
        Schedule(() => EventReceived?.Invoke(execution));
        Schedule(() => EventReceived?.Invoke(positionEvent));
    }

    /// <summary>The book's position implied by its own fills. Called under the gate.</summary>
    private ScaledQuantity InferredPosition()
    {
        long units = 0;
        foreach (var order in _orders.Values)
            units = checked(units + (order.CurrentTerms.Side == OrderSide.Buy ? order.FilledUnits : -order.FilledUnits));
        return ScaledQuantity.FromWhole(units);
    }

    private async Task SubmitAsync(ClientOrderId clientOrderId, RouteOrderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var order = await _route.SubmitAsync(Environment, request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // The acknowledgement may not echo our id; bind it to this order explicitly.
            OnOrderUpdated(string.IsNullOrEmpty(order.ClientOrderId) || order.ClientOrderId != clientOrderId.Value
                ? order with { ClientOrderId = clientOrderId.Value }
                : order);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (BrokerOrderRouteException exception) when (exception.IsRejection)
        {
            PublishTerminal(clientOrderId, VenueEventKind.Rejected, SafeReason(exception));
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private async Task CancelAsync(ClientOrderId clientOrderId, RouteOrder target, CancellationToken cancellationToken)
    {
        try
        {
            await _route.CancelAsync(Environment, target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Confirmation arrives through the poll; a broker that answers the cancel with the final state is
            // read at once rather than a poll interval later.
            var order = await _route.OrderAsync(Environment, Symbol, target.OrderId, target.ClientOrderId, cancellationToken).ConfigureAwait(false);
            if (order is not null)
                OnOrderUpdated(order);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown,
                $"{DisplayName} did not confirm the cancellation; the order needs reconciliation: {SafeReason(exception)}");
        }
    }

    private async Task ReplaceAsync(
        ClientOrderId clientOrderId,
        RouteOrder target,
        decimal quantity,
        decimal? limitPrice,
        decimal? stopPrice,
        CanonicalOrderTerms replacementTerms,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await _route.ReplaceAsync(Environment, target, quantity, limitPrice, stopPrice, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TrackedOrder? tracked;
            lock (_gate)
            {
                if (!_orders.TryGetValue(clientOrderId, out tracked))
                    return;
                if (!string.IsNullOrEmpty(order.OrderId))
                    RememberBrokerId(tracked, order.OrderId);
                tracked.CurrentTerms = replacementTerms;
                tracked.PendingReplacement = null;
                tracked.State = tracked.FilledUnits == 0 ? OrderLifecycleState.Working : OrderLifecycleState.PartiallyFilled;
            }
            PublishOrderEvent(tracked, VenueEventKind.Replaced, order.UpdatedAtUtc, replacementTerms: replacementTerms);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown,
                $"{DisplayName} did not confirm the replacement; the original order needs reconciliation: {SafeReason(exception)}");
        }
    }

    private void PublishTerminal(ClientOrderId clientOrderId, VenueEventKind kind, string? reason, DateTime? occurredAtUtc = null)
    {
        TrackedOrder? tracked;
        lock (_gate)
        {
            if (!_orders.TryGetValue(clientOrderId, out tracked))
                return;
            tracked.State = kind switch
            {
                VenueEventKind.Cancelled => OrderLifecycleState.Cancelled,
                VenueEventKind.Rejected => OrderLifecycleState.Rejected,
                VenueEventKind.Expired => OrderLifecycleState.Expired,
                VenueEventKind.OutcomeUnknown => OrderLifecycleState.Unknown,
                _ => tracked.State,
            };
        }
        PublishOrderEvent(tracked, kind, occurredAtUtc ?? UtcNow(), reason: reason);
    }

    private void PublishOrderEvent(
        TrackedOrder tracked,
        VenueEventKind kind,
        DateTime occurredAtUtc,
        CanonicalOrderTerms? replacementTerms = null,
        string? reason = null)
    {
        var venueEvent = VenueEvent(tracked, kind, occurredAtUtc, replacementTerms: replacementTerms, reason: reason);
        var orderEvent = new BrokerOrderEvent(
            EventId(tracked, kind.ToString(), occurredAtUtc, tracked.FilledUnits),
            Account,
            tracked.Instruction.Identity.ClientOrderId,
            Utc(occurredAtUtc),
            venueEvent);
        Schedule(() => EventReceived?.Invoke(orderEvent));
    }

    private VenueEvent VenueEvent(
        TrackedOrder tracked,
        VenueEventKind kind,
        DateTime occurredAtUtc,
        FillExecution? fill = null,
        CanonicalOrderTerms? replacementTerms = null,
        string? reason = null) =>
        new(kind,
            tracked.Instruction.Identity.ClientOrderId,
            tracked.BrokerOrderId,
            null,
            fill,
            replacementTerms,
            Utc(occurredAtUtc),
            tracked.CausationId,
            new DeduplicationKey(EventId(tracked, kind.ToString(), occurredAtUtc, tracked.FilledUnits).Value),
            reason);

    // ── Mapping ──────────────────────────────────────────────────────────────────────────────────

    private bool TryMapRequest(CanonicalOrderInstruction instruction, out RouteOrderRequest? request, out string? reason)
    {
        request = null;
        reason = null;
        var terms = instruction.Terms;
        if (!TryNative(terms.Quantity, out var quantity))
        {
            reason = $"The quantity has no exact {DisplayName} representation.";
            return false;
        }

        request = new RouteOrderRequest(
            Symbol,
            instruction.Identity.ClientOrderId.Value,
            terms.Side,
            terms.OrderType switch
            {
                CanonicalOrderType.Limit => RouteOrderType.Limit,
                CanonicalOrderType.Stop => RouteOrderType.Stop,
                CanonicalOrderType.StopLimit => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            terms.TimeInForce switch
            {
                CanonicalTimeInForce.GoodTillCancelled => RouteTimeInForce.GoodTillCancelled,
                CanonicalTimeInForce.ImmediateOrCancel => RouteTimeInForce.ImmediateOrCancel,
                CanonicalTimeInForce.FillOrKill => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            quantity,
            RouteValues.ToDecimal(terms.LimitPrice),
            RouteValues.ToDecimal(terms.StopPrice));
        return true;
    }

    private bool TryNative(ScaledQuantity units, out decimal native)
    {
        native = 0;
        RouteInstrument? rules;
        lock (_gate)
            rules = _rules;
        if (rules is null || !units.TryGetWholeUnits(out var whole) || whole <= 0)
            return false;
        native = whole * rules.UnitSize;
        return true;
    }

    private RouteOrder RouteOrderOf(TrackedOrder tracked) => new(
        tracked.BrokerOrderId?.Value ?? string.Empty,
        tracked.Instruction.Identity.ClientOrderId.Value,
        Symbol,
        tracked.CurrentTerms.Side,
        tracked.CurrentTerms.OrderType switch
        {
            CanonicalOrderType.Limit => RouteOrderType.Limit,
            CanonicalOrderType.Stop => RouteOrderType.Stop,
            CanonicalOrderType.StopLimit => RouteOrderType.StopLimit,
            _ => RouteOrderType.Market,
        },
        RouteTimeInForce.Day,
        tracked.CurrentTerms.Quantity.TryGetWholeUnits(out var units) && _rules is { } rules ? units * rules.UnitSize : 0,
        RouteValues.ToDecimal(tracked.CurrentTerms.LimitPrice),
        RouteValues.ToDecimal(tracked.CurrentTerms.StopPrice),
        RouteOrderStatus.Working,
        tracked.FilledNative,
        tracked.AveragePrice,
        0,
        string.Empty,
        null,
        UtcNow());

    private BrokerExecutionCapabilities? CapabilitiesFor(RouteInstrument rules)
    {
        if (!RouteValues.TryPrice(rules.TickSize, out var tick))
            return null;
        var types = SupportedOrderTypes.None;
        if (rules.OrderTypes.HasFlag(RouteOrderTypes.Market)) types |= SupportedOrderTypes.Market;
        if (rules.OrderTypes.HasFlag(RouteOrderTypes.Limit)) types |= SupportedOrderTypes.Limit;
        if (rules.OrderTypes.HasFlag(RouteOrderTypes.Stop)) types |= SupportedOrderTypes.Stop;
        if (rules.OrderTypes.HasFlag(RouteOrderTypes.StopLimit)) types |= SupportedOrderTypes.StopLimit;
        var tifs = SupportedTimeInForce.None;
        if (rules.TimesInForce.HasFlag(RouteTimesInForce.Day)) tifs |= SupportedTimeInForce.Day;
        if (rules.TimesInForce.HasFlag(RouteTimesInForce.GoodTillCancelled)) tifs |= SupportedTimeInForce.GoodTillCancelled;
        if (rules.TimesInForce.HasFlag(RouteTimesInForce.ImmediateOrCancel)) tifs |= SupportedTimeInForce.ImmediateOrCancel;
        if (rules.TimesInForce.HasFlag(RouteTimesInForce.FillOrKill)) tifs |= SupportedTimeInForce.FillOrKill;

        return new BrokerExecutionCapabilities(
            Version: $"{AdapterId}-{Symbol}-{rules.UnitSize}-{rules.TickSize}-{(int)types}-{(int)tifs}-{rules.SupportsReplace}-v1",
            CanonicalCapabilities: new VenueCapabilities(types, tifs),
            QuantityPrecision: 0,
            MinimumQuantity: ScaledQuantity.FromWhole(rules.MinimumUnits),
            MaximumQuantity: ScaledQuantity.FromWhole(rules.MaximumUnits),
            LotSize: ScaledQuantity.FromWhole(1),
            SupportsFractionalQuantity: false,
            PricePrecision: tick.Scale,
            TickSize: tick,
            MinimumPrice: tick,
            MaximumPrice: null,
            ReplaceSemantics: rules.SupportsReplace ? BrokerReplaceSemantics.InPlace : BrokerReplaceSemantics.Unsupported,
            SupportsNativeBracket: false,
            SupportsNativeOco: false,
            TradingHours: BrokerTradingHours.AlwaysOpen,
            RateLimit: new BrokerRateLimit(_options.MaximumCommandsPerMinute, TimeSpan.FromMinutes(1)));
    }

    private BrokerExecutionCapabilities UnavailableCapabilities() => new(
        Version: $"{AdapterId}-unavailable-v1",
        CanonicalCapabilities: new VenueCapabilities(SupportedOrderTypes.None, SupportedTimeInForce.None),
        QuantityPrecision: 0,
        MinimumQuantity: ScaledQuantity.FromWhole(1),
        MaximumQuantity: ScaledQuantity.FromWhole(1_000_000_000),
        LotSize: ScaledQuantity.FromWhole(1),
        SupportsFractionalQuantity: false,
        PricePrecision: 2,
        TickSize: new ScaledPrice(1, 2),
        MinimumPrice: new ScaledPrice(1, 2),
        MaximumPrice: null,
        ReplaceSemantics: BrokerReplaceSemantics.Unsupported,
        SupportsNativeBracket: false,
        SupportsNativeOco: false,
        TradingHours: BrokerTradingHours.AlwaysOpen,
        RateLimit: new BrokerRateLimit(_options.MaximumCommandsPerMinute, TimeSpan.FromMinutes(1)));

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private bool TryResolve(BrokerOrderQuery query, out ClientOrderId clientOrderId, out TrackedOrder? tracked)
    {
        lock (_gate)
        {
            if (query.ClientOrderId is { } supplied && _orders.TryGetValue(supplied, out tracked))
            {
                clientOrderId = supplied;
                return true;
            }
            if (query.BrokerOrderId is { } broker &&
                _brokerToClient.TryGetValue(broker, out clientOrderId) &&
                _orders.TryGetValue(clientOrderId, out tracked))
            {
                return true;
            }
        }
        clientOrderId = default;
        tracked = null;
        return false;
    }

    private void RememberBrokerId(TrackedOrder tracked, string orderId)
    {
        var brokerOrderId = new BrokerOrderId(orderId);
        if (!brokerOrderId.IsValid)
            return;
        if (_brokerToClient.TryGetValue(brokerOrderId, out var existing) && existing != tracked.Instruction.Identity.ClientOrderId)
            return;
        if (tracked.BrokerOrderId is { } prior && prior != brokerOrderId)
            _brokerToClient.Remove(prior);
        tracked.BrokerOrderId = brokerOrderId;
        _brokerToClient[brokerOrderId] = tracked.Instruction.Identity.ClientOrderId;
    }

    private bool MakeOrderCapacity()
    {
        while (_orders.Count >= _options.MaximumTrackedOrders && _insertionOrder.TryDequeue(out var oldest))
        {
            if (!_orders.TryGetValue(oldest, out var tracked) || !OrderLifecycle.IsTerminal(tracked.State))
            {
                _insertionOrder.Enqueue(oldest);
                return false;
            }
            _orders.Remove(oldest);
            if (tracked.BrokerOrderId is { } id)
                _brokerToClient.Remove(id);
        }
        return _orders.Count < _options.MaximumTrackedOrders;
    }

    private bool TryConsumeRateBudget()
    {
        lock (_gate)
        {
            var now = UtcNow();
            if (now < _rateWindowStartedUtc || now - _rateWindowStartedUtc >= TimeSpan.FromMinutes(1))
            {
                _rateWindowStartedUtc = now;
                _commandsInRateWindow = 0;
            }
            if (_commandsInRateWindow >= _options.MaximumCommandsPerMinute)
                return false;
            _commandsInRateWindow++;
            return true;
        }
    }

    private bool TryGetConnectionToken(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_connectionLifetime is { IsCancellationRequested: false } lifetime)
            {
                token = lifetime.Token;
                return true;
            }
        }
        token = new CancellationToken(canceled: true);
        return false;
    }

    private bool TryValidateLiveAuthorization(out string? reason)
    {
        reason = null;
        if (Mode == ExecutionMode.Paper)
            return true;
        try
        {
            _ = LiveExecutionAuthorizationGate.Require(
                _options.AllowLiveExecution, _hasCredentials, _route.RouteId, _expectedAccountId, _confirmationStore);
            return true;
        }
        catch (Exception exception)
        {
            reason = $"{DisplayName} LIVE authorization is absent or was revoked: {SafeReason(exception)}";
            return false;
        }
    }

    private BrokerAdapterCommandResult RejectRevokedLiveAuthorization(string reason)
    {
        CloseUnauthorizedSession();
        return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, reason);
    }

    private void CloseUnauthorizedSession()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
            lifetime = _connectionLifetime;
        try
        {
            lifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
    }

    private void TrackOperation(Task operation)
    {
        lock (_gate)
            _pendingOperations.Add(operation);
        _ = operation.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _pendingOperations.Remove(completed);
                    _pendingOperationSlots--;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task AwaitPendingOperationsAsync()
    {
        Task[] operations;
        lock (_gate)
            operations = _pendingOperations.ToArray();
        if (operations.Length == 0)
            return;
        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Waits until every callback scheduled so far has run. A scheduler that never drains (the
    /// deterministic test scheduler) is left alone after the timeout rather than hanging the caller.</summary>
    private async Task DrainSchedulerAsync(CancellationToken cancellationToken)
    {
        if (_scheduler is not SerializedAdapterEventScheduler)
            return;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(() => drained.TrySetResult());
        await drained.Task.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
    }

    private void OnSchedulerFaulted(Exception exception) =>
        SetSession(ExecutionSessionHealth.Degraded, Session.IsDataConnected, Session.IsExecutionAuthenticated, false);

    private void SetSession(ExecutionSessionHealth health, bool isDataConnected, bool isExecutionAuthenticated, bool isExecutionCertified)
    {
        lock (_gate)
            _session = new BrokerExecutionSession(Account, health, isDataConnected, isExecutionAuthenticated, isExecutionCertified, UtcNow());
    }

    private void Schedule(Action callback)
    {
        if (_disposed)
            return;
        try
        {
            _scheduler.Schedule(() =>
            {
                if (!_disposed)
                    callback();
            });
        }
        catch
        {
            SetSession(ExecutionSessionHealth.Degraded, Session.IsDataConnected, Session.IsExecutionAuthenticated, false);
        }
    }

    private void ClearReferencePrice()
    {
        LatestReferencePrice = null;
        LatestReferencePriceObservedAtUtc = null;
        LatestReferencePriceFetchedAtUtc = null;
    }

    private BrokerDispatchReceipt CreateReceipt(BrokerAdapterCommandKind kind, ClientOrderId clientOrderId, CausationId causationId)
    {
        var material = $"{AdapterId}|{Account.AccountId.Value}|{kind}|{clientOrderId.Value}|{causationId.Value}";
        return new BrokerDispatchReceipt(
            new DispatchReceiptId($"{_route.RouteId}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}"),
            Account, kind, clientOrderId, causationId, UtcNow());
    }

    private BrokerAdapterEventId EventId(TrackedOrder tracked, string kind, DateTime occurredAtUtc, long units)
    {
        var material = $"{AdapterId}|{tracked.Instruction.Identity.ClientOrderId.Value}|{tracked.BrokerOrderId?.Value}|{kind}|{Utc(occurredAtUtc).Ticks}|{units}";
        return new BrokerAdapterEventId($"{_route.RouteId}-event-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))}");
    }

    private static BrokerAdapterCommandResult Dispatched(BrokerDispatchReceipt receipt) =>
        new(BrokerAdapterCommandStatus.Dispatched, BrokerAdapterCommandFault.None, receipt, 0, null);

    private static BrokerAdapterCommandResult Rejected(BrokerAdapterCommandFault fault, string reason) =>
        new(BrokerAdapterCommandStatus.RejectedBeforeDispatch, fault, null, 0, reason);

    private static BrokerAdapterCommandResult AdmissionRejected(ExecutionAdmissionResult admission) =>
        Rejected(
            admission.Fault is ExecutionAdmissionFault.DataDisconnected or ExecutionAdmissionFault.ExecutionNotAuthenticated or
                ExecutionAdmissionFault.ExecutionNotCertified or ExecutionAdmissionFault.SessionUnavailable or ExecutionAdmissionFault.InvalidSession
                ? BrokerAdapterCommandFault.ExecutionUnavailable
                : BrokerAdapterCommandFault.UnsupportedCapability,
            admission.Reason ?? admission.Fault.ToString());

    private static VenueOrderSnapshot ToVenueSnapshot(TrackedOrder tracked) => new(
        tracked.Instruction,
        tracked.CurrentTerms,
        tracked.State,
        tracked.BrokerOrderId,
        null,
        ScaledQuantity.FromWhole(tracked.FilledUnits));

    private BrokerReconciliationSnapshot EmptySnapshot(DateTime capturedAtUtc) => new(
        Account,
        capturedAtUtc,
        Array.AsReadOnly<VenueOrderSnapshot>([]),
        Array.AsReadOnly<VenueOrderSnapshot>([]),
        Array.AsReadOnly<BrokerPositionSnapshot>([]),
        Array.AsReadOnly<BrokerCashSnapshot>([]));

    private static BrokerReconciliationSnapshot CopySnapshot(BrokerReconciliationSnapshot snapshot) => new(
        snapshot.Account,
        snapshot.CapturedAtUtc,
        new ReadOnlyCollection<VenueOrderSnapshot>(snapshot.OpenOrders.ToArray()),
        new ReadOnlyCollection<VenueOrderSnapshot>(snapshot.CompletedOrders.ToArray()),
        new ReadOnlyCollection<BrokerPositionSnapshot>(snapshot.Positions.ToArray()),
        new ReadOnlyCollection<BrokerCashSnapshot>(snapshot.Cash.ToArray()));

    private DateTime UtcNow() => Utc(_clock.UtcNow);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string SafeReason(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
        return message.Length <= 256 ? message : message[..256];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal continues; the session is closed below either way.
        }
        _disposed = true;
        _lifetime.Cancel();
        try
        {
            await AwaitPendingOperationsAsync().ConfigureAwait(false);
            if (_ownedScheduler is not null)
            {
                _ownedScheduler.CallbackFaulted -= OnSchedulerFaulted;
                await _ownedScheduler.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifetime.Dispose();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    private sealed class TrackedOrder(CanonicalOrderInstruction instruction, CanonicalOrderTerms currentTerms, CausationId causationId)
    {
        internal CanonicalOrderInstruction Instruction { get; } = instruction;
        internal CanonicalOrderTerms CurrentTerms { get; set; } = currentTerms;
        internal OrderLifecycleState State { get; set; } = OrderLifecycleState.Acknowledging;
        internal BrokerOrderId? BrokerOrderId { get; set; }
        internal long FilledUnits { get; set; }
        internal decimal FilledNative { get; set; }
        internal decimal? AveragePrice { get; set; }
        internal decimal QuoteFee { get; set; }
        internal decimal BaseAssetFee { get; set; }
        internal CausationId CausationId { get; set; } = causationId;
        internal CanonicalOrderTerms? PendingReplacement { get; set; }
        internal bool Acknowledged { get; set; }
    }
}

/// <summary>Bounded single-consumer scheduler for routed adapter callbacks: in order, never inline.</summary>
public sealed class SerializedAdapterEventScheduler : IAdapterEventScheduler, IAsyncDisposable
{
    private const int CallbackCapacity = 4_096;
    private readonly Channel<Action> _callbacks = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(CallbackCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _consumer;
    private volatile bool _completed;

    public SerializedAdapterEventScheduler() => _consumer = Task.Run(ConsumeAsync);

    public event Action<Exception>? CallbackFaulted;

    public void Schedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_callbacks.Writer.TryWrite(callback))
            return;
        if (_completed)
            throw new ObjectDisposedException(nameof(SerializedAdapterEventScheduler));
        // Overflow faults the adapter rather than dropping an event: a lost fill is worse than a stopped book.
        var fault = new InvalidOperationException("The routed execution-event queue overflowed.");
        CallbackFaulted?.Invoke(fault);
        throw fault;
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var callback in _callbacks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                CallbackFaulted?.Invoke(exception);
            }
        }
    }
}
