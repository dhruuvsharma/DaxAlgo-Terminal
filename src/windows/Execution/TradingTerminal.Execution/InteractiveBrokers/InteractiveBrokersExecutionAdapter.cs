using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>
/// TWS execution adapter for one exact IB account and contract. All SDK and socket
/// details remain behind <see cref="IInteractiveBrokersExecutionTransport"/>.
/// </summary>
public sealed class InteractiveBrokersExecutionAdapter : IBrokerExecutionAdapter, IDisposable, IAsyncDisposable
{
    public const string StableAdapterId = "interactive-brokers-paper";
    public const string LiveAdapterId = "interactive-brokers-live";
    private const int MaximumPendingOperations = 512;

    private readonly object _gate = new();
    private readonly InteractiveBrokersExecutionOptions _options;
    private readonly InteractiveBrokersExecutionEndpoint _endpoint;
    private readonly ILiveExecutionConfirmationStore? _liveConfirmationStore;
    private readonly IInteractiveBrokersExecutionTransport _transport;
    private readonly IClock _clock;
    private readonly IAdapterEventScheduler _scheduler;
    private readonly InteractiveBrokersSerializedEventScheduler? _ownedScheduler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ClientOrderId, TrackedOrder> _orders = [];
    private readonly Dictionary<int, ClientOrderId> _nativeToClient = [];
    private readonly Dictionary<BrokerOrderId, ClientOrderId> _brokerToClient = [];
    private readonly Queue<ClientOrderId> _insertionOrder = new();
    private readonly Dictionary<string, InteractiveBrokersExecutionSnapshot> _pendingExecutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InteractiveBrokersCommissionSnapshot> _pendingCommissions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedExecutions = new(StringComparer.Ordinal);
    private readonly Queue<string> _publishedExecutionOrder = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private CancellationTokenSource? _connectionLifetime;
    private BrokerExecutionSession _session;
    private BrokerExecutionCapabilities _capabilities;
    private BrokerReconciliationSnapshot _snapshot;
    private DateTime _rateWindowStartedUtc;
    private int _commandsInRateWindow;
    private int _pendingOperationSlots;
    private bool _disposed;
    private ClientOrderId? _lastPositionClientOrderId;

    public InteractiveBrokersExecutionAdapter(
        InteractiveBrokersExecutionOptions options,
        IInteractiveBrokersExecutionTransport transport,
        IClock clock,
        IAdapterEventScheduler scheduler,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        if (!_options.Enabled)
            throw new InvalidOperationException("Interactive Brokers execution must be explicitly enabled before constructing the adapter.");
        _liveConfirmationStore = confirmationStore;
        _endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(_options, confirmationStore);
        var configurationFault = _options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            throw new InvalidOperationException(configurationFault);
        if (!LiveExecutionConfirmation.IsLookupValid(InteractiveBrokersExecutionOptions.BrokerId, _options.AccountId) ||
            !string.Equals(_options.AccountId, _options.AccountId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Interactive Brokers execution requires one exact configured account ID so the OMS account binding cannot change after connection.");
        }

        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (_transport.Endpoint != _endpoint || !_transport.Endpoint.IsAuthorized)
            throw new InvalidOperationException("The injected IB transport does not identify the exact gated endpoint.");
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _ownedScheduler = scheduler as InteractiveBrokersSerializedEventScheduler;

        Contract = new InteractiveBrokersContract(
            _options.ContractId,
            _options.Symbol.Trim().ToUpperInvariant(),
            _options.SecurityType.Trim().ToUpperInvariant(),
            _options.Exchange.Trim().ToUpperInvariant(),
            _options.PrimaryExchange.Trim().ToUpperInvariant(),
            _options.Currency.Trim().ToUpperInvariant());
        Instrument = new InstrumentId(_options.CanonicalInstrumentId);
        Account = new BrokerExecutionAccount(
            new ExecutionAdapterId(AdapterId),
            new BrokerAccountId(_options.AccountId));
        var now = UtcNow();
        _session = new BrokerExecutionSession(Account, ExecutionSessionHealth.Disconnected, false, false, false, now);
        _capabilities = UnavailableCapabilities();
        NativeCapabilities = UnavailableNativeCapabilities(now);
        _snapshot = EmptySnapshot(DateTime.UnixEpoch);
        _rateWindowStartedUtc = now;

        _transport.OrderUpdated += OnOrderUpdated;
        _transport.ExecutionReceived += OnExecutionReceived;
        _transport.CommissionReceived += OnCommissionReceived;
        _transport.PositionUpdated += OnPositionUpdated;
        _transport.OrderError += OnOrderError;
        _transport.Faulted += OnTransportFaulted;
        if (_ownedScheduler is not null)
            _ownedScheduler.CallbackFaulted += OnSchedulerFaulted;
    }

    public string BrokerId => InteractiveBrokersExecutionOptions.BrokerId;

    public ExecutionMode Mode => _endpoint.Mode;

    public string AdapterId => Mode == ExecutionMode.Live ? LiveAdapterId : StableAdapterId;

    public BrokerExecutionAccount Account { get; }

    public InstrumentId Instrument { get; }

    public string Symbol => Contract.Symbol;

    public InteractiveBrokersContract Contract { get; }

    public string? NativeAccountId { get; private set; }

    public InteractiveBrokersNativeCapabilities NativeCapabilities { get; private set; }

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

    /// <summary>Cash evidence has no shared adapter-event envelope, so consumers may observe it separately.</summary>
    public event Action<BrokerCashSnapshot>? CashSnapshotReceived;

    /// <summary>Connects the one reusable socket and certifies the exact configured account/contract.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateLiveAuthorization(out var authorizationFault))
        {
            CloseUnauthorizedSession();
            throw new InvalidOperationException(authorizationFault);
        }

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource connectionLifetime;
        lock (_gate)
        {
            _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectionLifetime = _connectionLifetime;
        }
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionLifetime.Token);
        try
        {
            var nativeSession = await _transport
                .ConnectAsync(_options.ClientId, _options.AccountId, attempt.Token)
                .ConfigureAwait(false);
            ValidateSession(nativeSession);
            NativeAccountId = nativeSession.AccountId;
            SetSession(ExecutionSessionHealth.Degraded, true, true, false);

            var nativeCapabilities = await _transport
                .DiscoverCapabilitiesAsync(Contract, attempt.Token)
                .ConfigureAwait(false);
            ValidateNativeCapabilities(nativeCapabilities);
            NativeCapabilities = nativeCapabilities;
            lock (_gate)
                _capabilities = DiscoverCanonicalCapabilities(nativeCapabilities);

            await RefreshReconciliationAsync(attempt.Token).ConfigureAwait(false);
            SetSession(ExecutionSessionHealth.Healthy, true, true, true);
        }
        catch
        {
            await SafeDisconnectTransportAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_connectionLifetime, connectionLifetime))
                    _connectionLifetime = null;
            }
            connectionLifetime.Cancel();
            connectionLifetime.Dispose();
            NativeAccountId = null;
            NativeCapabilities = UnavailableNativeCapabilities(UtcNow());
            lock (_gate)
                _capabilities = UnavailableCapabilities();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        CancellationTokenSource? connectionLifetime;
        lock (_gate)
        {
            connectionLifetime = _connectionLifetime;
            _connectionLifetime = null;
        }
        connectionLifetime?.Cancel();
        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionLifetime?.Dispose();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command)
    {
        if (command is null || command.Instruction is null || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The IB submit command is invalid or uses stale capabilities.");
        }
        if (!TryAuthorizeCommand(command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "IB LIVE submit requires a current one-use OMS guardrail admission.");
        if (command.Instruction.TradeIntent.Instrument != Instrument)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "This IB adapter is certified for one configured contract only.");
        if (command.Instruction.Identity.ClientOrderId.Value.Length > 64)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "IB OrderRef/clientOrderId is limited to 64 characters by this adapter.");
        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, command.Instruction, UtcNow());
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local IB command budget is exhausted.");
        if (!TryMapRequest(command.Instruction, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The IB connection lifetime has ended.");

        var clientOrderId = command.Instruction.Identity.ClientOrderId;
        int nativeOrderId;
        try
        {
            nativeOrderId = _transport.ReserveOrderId();
        }
        catch (Exception exception)
        {
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, SafeReason(exception));
        }
        if (nativeOrderId <= 0)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "IB did not provide a valid next order ID.");

        lock (_gate)
        {
            if (_orders.ContainsKey(clientOrderId) || _nativeToClient.ContainsKey(nativeOrderId))
            {
                return new BrokerAdapterCommandResult(
                    BrokerAdapterCommandStatus.Conflict,
                    BrokerAdapterCommandFault.Conflict,
                    null,
                    0,
                    "The client or native order ID is already bound to an IB order.");
            }
            if (!MakeOrderCapacity() || _pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded IB order/operation table is full.");

            var brokerOrderId = NativeBrokerOrderId(nativeOrderId);
            var tracked = new TrackedOrder(
                command.Instruction,
                command.Instruction.Terms,
                OrderLifecycleState.Acknowledging,
                nativeOrderId,
                brokerOrderId,
                ScaledQuantity.Zero,
                command.CausationId);
            _orders.Add(clientOrderId, tracked);
            _nativeToClient.Add(nativeOrderId, clientOrderId);
            _brokerToClient.Add(brokerOrderId, clientOrderId);
            _insertionOrder.Enqueue(clientOrderId);
            _pendingOperationSlots++;
        }

        request = request! with { OrderId = nativeOrderId };
        TrackOperation(PlaceAsync(clientOrderId, request, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Submit, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid)
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The IB cancel command is invalid.");
        if (!TryAuthorizeCommand(command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "IB LIVE cancel requires a current one-use OMS guardrail admission.");
        if (!Session.CanExecute)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The IB session cannot execute.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked))
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching IB order is known.");
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local IB command budget is exhausted.");
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The IB connection lifetime has ended.");
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded IB command-operation table is full.");
            tracked!.State = OrderLifecycleState.PendingCancel;
            tracked.CausationId = command.CausationId;
            _pendingOperationSlots++;
        }
        TrackOperation(CancelAsync(clientOrderId, tracked!.NativeOrderId, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Cancel, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The IB replace command is invalid or uses stale capabilities.");
        }
        if (!TryAuthorizeCommand(command))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "IB LIVE replace requires a current one-use OMS guardrail admission.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked))
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching IB order is known.");
        var replacementInstruction = tracked!.Instruction with { Terms = command.ReplacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, replacementInstruction, UtcNow(), isReplace: true);
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local IB command budget is exhausted.");
        if (!TryMapRequest(replacementInstruction, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The IB connection lifetime has ended.");
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded IB command-operation table is full.");
            tracked.State = OrderLifecycleState.PendingReplace;
            tracked.CausationId = command.CausationId;
            tracked.PendingReplacement = command.ReplacementTerms;
            _pendingOperationSlots++;
        }
        request = request! with { OrderId = tracked.NativeOrderId };
        TrackOperation(ModifyAsync(clientOrderId, request, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId));
    }

    public BrokerOrderQueryResult Query(BrokerOrderQuery query)
    {
        if (!query.IsValid)
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.InvalidCommand, null, "The IB query is invalid.");
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
        if (!_transport.IsConnected)
            throw new InvalidOperationException("The IB transport is disconnected.");
        var native = await _transport
            .GetReconciliationSnapshotAsync(_options.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(native.AccountId, _options.AccountId, StringComparison.Ordinal) ||
            native.CapturedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new InvalidDataException("The IB reconciliation snapshot belongs to another account or lacks UTC time.");
        }

        var open = native.OpenOrders
            .Where(item =>
                string.Equals(item.AccountId, _options.AccountId, StringComparison.Ordinal) &&
                ContractMatches(item.Contract))
            .Select(BuildVenueSnapshot)
            .ToArray();
        var completed = native.CompletedOrders
            .Where(item =>
                string.Equals(item.AccountId, _options.AccountId, StringComparison.Ordinal) &&
                ContractMatches(item.Contract))
            .Select(BuildVenueSnapshot)
            .ToArray();
        var positions = native.Positions
            .Where(item => ContractMatches(item.Contract) && string.Equals(item.AccountId, _options.AccountId, StringComparison.Ordinal))
            .Select(item => new BrokerPositionSnapshot(Instrument, item.Quantity, Utc(item.ObservedAtUtc)))
            .ToArray();
        if (positions.Length > 1)
            throw new InvalidDataException("IB returned duplicate positions for the configured contract.");
        var cash = native.Cash
            .Where(item => string.Equals(item.AccountId, _options.AccountId, StringComparison.Ordinal))
            .Select(item => new BrokerCashSnapshot(item.Currency, item.TotalCash, item.AvailableFunds, Utc(item.ObservedAtUtc)))
            .ToArray();
        if (cash.GroupBy(item => item.Currency, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("IB returned duplicate cash rows for one currency.");

        var snapshot = new BrokerReconciliationSnapshot(
            Account,
            native.CapturedAtUtc,
            Array.AsReadOnly(open),
            Array.AsReadOnly(completed),
            Array.AsReadOnly(positions),
            Array.AsReadOnly(cash));
        lock (_gate)
            _snapshot = snapshot;
        foreach (var item in cash)
            Schedule(() => CashSnapshotReceived?.Invoke(item));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_gate)
        {
            _connectionLifetime?.Cancel();
            _connectionLifetime?.Dispose();
            _connectionLifetime = null;
        }
        Unsubscribe();
        try
        {
            AwaitPendingOperationsAsync().GetAwaiter().GetResult();
            _transport.Dispose();
            _ownedScheduler?.Dispose();
        }
        finally
        {
            _lifetime.Dispose();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_gate)
        {
            _connectionLifetime?.Cancel();
            _connectionLifetime?.Dispose();
            _connectionLifetime = null;
        }
        Unsubscribe();
        try
        {
            await AwaitPendingOperationsAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
            if (_ownedScheduler is not null)
                await _ownedScheduler.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    private async Task PlaceAsync(
        ClientOrderId clientOrderId,
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await _transport.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private async Task CancelAsync(ClientOrderId clientOrderId, int nativeOrderId, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await _transport.CancelOrderAsync(nativeOrderId, clientOrderId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private async Task ModifyAsync(
        ClientOrderId clientOrderId,
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await _transport.ModifyOrderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private void OnOrderUpdated(InteractiveBrokersOrderSnapshot update) =>
        Schedule(() => ProcessOrderUpdated(update));

    private void OnExecutionReceived(InteractiveBrokersExecutionSnapshot execution) =>
        Schedule(() => ProcessExecution(execution));

    private void OnCommissionReceived(InteractiveBrokersCommissionSnapshot commission) =>
        Schedule(() => ProcessCommission(commission));

    private void OnPositionUpdated(InteractiveBrokersPositionSnapshot position) =>
        Schedule(() => ProcessPosition(position));

    private void OnOrderError(InteractiveBrokersOrderError error) =>
        Schedule(() => ProcessOrderError(error));

    private void OnTransportFaulted(Exception exception) =>
        SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, Session.IsExecutionAuthenticated, false);

    private void OnSchedulerFaulted(Exception exception) =>
        SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, Session.IsExecutionAuthenticated, false);

    private void ProcessOrderUpdated(InteractiveBrokersOrderSnapshot update)
    {
        if (_disposed || !string.Equals(update.AccountId, _options.AccountId, StringComparison.Ordinal) ||
            !ContractMatches(update.Contract))
            return;
        if (!TryResolveNative(update.OrderId, update.ClientOrderId, out var tracked))
            return;

        CanonicalOrderTerms? mappedTerms = null;
        if (TryMapTerms(update, out var terms))
            mappedTerms = terms;
        var publishKind = (VenueEventKind?)null;
        CanonicalOrderTerms? replacement = null;
        lock (_gate)
        {
            RememberNativeIdentity(tracked!, update.OrderId);
            if (mappedTerms.HasValue)
            {
                if (tracked!.State == OrderLifecycleState.PendingReplace &&
                    tracked.PendingReplacement is { } pending && pending == mappedTerms.Value &&
                    update.Status is InteractiveBrokersNativeOrderStatus.PreSubmitted or InteractiveBrokersNativeOrderStatus.Submitted)
                {
                    tracked.CurrentTerms = pending;
                    tracked.PendingReplacement = null;
                    tracked.State = tracked.FilledQuantity.Coefficient == 0
                        ? OrderLifecycleState.Working
                        : OrderLifecycleState.PartiallyFilled;
                    replacement = pending;
                    publishKind = VenueEventKind.Replaced;
                }
                else if (tracked.PendingReplacement is null)
                {
                    tracked.CurrentTerms = mappedTerms.Value;
                }
            }

            if (publishKind is null)
            {
                switch (update.Status)
                {
                    case InteractiveBrokersNativeOrderStatus.PreSubmitted:
                    case InteractiveBrokersNativeOrderStatus.Submitted:
                        if (!tracked!.Acknowledged)
                        {
                            tracked.Acknowledged = true;
                            tracked.State = tracked.FilledQuantity.Coefficient == 0
                                ? OrderLifecycleState.Working
                                : OrderLifecycleState.PartiallyFilled;
                            publishKind = VenueEventKind.Acknowledged;
                        }
                        break;
                    case InteractiveBrokersNativeOrderStatus.PendingCancel:
                        tracked!.State = OrderLifecycleState.PendingCancel;
                        break;
                    case InteractiveBrokersNativeOrderStatus.Cancelled:
                    case InteractiveBrokersNativeOrderStatus.ApiCancelled:
                        if (tracked!.State != OrderLifecycleState.Cancelled)
                        {
                            tracked.State = OrderLifecycleState.Cancelled;
                            publishKind = VenueEventKind.Cancelled;
                        }
                        break;
                    case InteractiveBrokersNativeOrderStatus.Inactive:
                    case InteractiveBrokersNativeOrderStatus.Rejected:
                        if (tracked!.State != OrderLifecycleState.Rejected)
                        {
                            tracked.State = OrderLifecycleState.Rejected;
                            publishKind = VenueEventKind.Rejected;
                        }
                        break;
                }
            }
        }
        if (publishKind.HasValue)
        {
            PublishOrderEvent(
                tracked!,
                publishKind.Value,
                update.UpdatedAtUtc,
                replacement,
                update.RejectionReason ?? update.WhyHeld);
        }
    }

    private void ProcessExecution(InteractiveBrokersExecutionSnapshot execution)
    {
        if (_disposed || string.IsNullOrWhiteSpace(execution.ExecutionId) ||
            !string.Equals(execution.AccountId, _options.AccountId, StringComparison.Ordinal) ||
            !ContractMatches(execution.Contract))
            return;
        lock (_gate)
        {
            if (_publishedExecutions.Contains(execution.ExecutionId))
                return;
            if (_pendingExecutions.Count >= _options.MaximumTrackedOrders * 4 &&
                !_pendingExecutions.ContainsKey(execution.ExecutionId))
            {
                SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, true, false);
                return;
            }
            _pendingExecutions[execution.ExecutionId] = execution;
        }
        TryPublishExecution(execution.ExecutionId);
    }

    private void ProcessCommission(InteractiveBrokersCommissionSnapshot commission)
    {
        if (_disposed || string.IsNullOrWhiteSpace(commission.ExecutionId))
            return;
        lock (_gate)
        {
            if (_publishedExecutions.Contains(commission.ExecutionId))
                return;
            if (_pendingCommissions.Count >= _options.MaximumTrackedOrders * 4 &&
                !_pendingCommissions.ContainsKey(commission.ExecutionId))
            {
                SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, true, false);
                return;
            }
            _pendingCommissions[commission.ExecutionId] = commission;
        }
        TryPublishExecution(commission.ExecutionId);
    }

    private void TryPublishExecution(string executionId)
    {
        InteractiveBrokersExecutionSnapshot execution;
        InteractiveBrokersCommissionSnapshot commission;
        TrackedOrder? tracked;
        FillExecution fill;
        lock (_gate)
        {
            if (_publishedExecutions.Contains(executionId) ||
                !_pendingExecutions.TryGetValue(executionId, out execution!) ||
                !_pendingCommissions.TryGetValue(executionId, out commission!) ||
                !TryResolveNativeLocked(execution.OrderId, execution.ClientOrderId, out tracked))
            {
                return;
            }
            if (!execution.Quantity.TryGetWholeUnits(out var fillUnits) || fillUnits <= 0 ||
                commission.CommissionAndFees.Coefficient < 0 ||
                !TryAdd(tracked!.FilledQuantity, execution.Quantity, out var cumulative) ||
                !cumulative.TryGetWholeUnits(out var cumulativeUnits) ||
                !tracked.CurrentTerms.Quantity.TryGetWholeUnits(out var requestedUnits) ||
                cumulativeUnits > requestedUnits)
            {
                tracked!.State = OrderLifecycleState.Unknown;
                _pendingExecutions.Remove(executionId);
                _pendingCommissions.Remove(executionId);
                PublishOrderEvent(
                    tracked,
                    VenueEventKind.OutcomeUnknown,
                    execution.ObservedAtUtc,
                    reason: "The IB execution/commission pair could not be represented exactly.");
                return;
            }

            tracked.FilledQuantity = cumulative;
            tracked.Acknowledged = true;
            tracked.State = cumulativeUnits == requestedUnits
                ? OrderLifecycleState.Filled
                : OrderLifecycleState.PartiallyFilled;
            _lastPositionClientOrderId = tracked.Instruction.Identity.ClientOrderId;
            _pendingExecutions.Remove(executionId);
            _pendingCommissions.Remove(executionId);
            RememberPublishedExecution(executionId);
            var liquidity = execution.Side.Contains("ADD", StringComparison.OrdinalIgnoreCase)
                ? LiquidityFlag.Maker
                : LiquidityFlag.Taker;
            fill = new FillExecution(execution.Quantity, execution.Price, commission.CommissionAndFees, liquidity);
        }

        var occurredAtUtc = Utc(execution.ObservedAtUtc);
        var venueEvent = CreateVenueEvent(tracked!, VenueEventKind.Fill, occurredAtUtc, fill: fill);
        EventReceived?.Invoke(new BrokerExecutionEvent(
            EventId(tracked!, $"exec-{executionId}", occurredAtUtc, tracked!.FilledQuantity),
            Account,
            tracked.Instruction.Identity.ClientOrderId,
            occurredAtUtc,
            venueEvent));
        EventReceived?.Invoke(new BrokerCommissionEvent(
            EventId(tracked, $"commission-{executionId}", Utc(commission.ObservedAtUtc), tracked.FilledQuantity),
            Account,
            tracked.Instruction.Identity.ClientOrderId,
            Utc(commission.ObservedAtUtc),
            tracked.CausationId,
            commission.CommissionAndFees));
    }

    private void ProcessPosition(InteractiveBrokersPositionSnapshot position)
    {
        if (_disposed || !string.Equals(position.AccountId, _options.AccountId, StringComparison.Ordinal) ||
            !ContractMatches(position.Contract))
            return;
        ClientOrderId? clientOrderId;
        lock (_gate)
        {
            var observedAtUtc = Utc(position.ObservedAtUtc);
            var positions = _snapshot.Positions
                .Where(item => item.Instrument != Instrument)
                .Append(new BrokerPositionSnapshot(Instrument, position.Quantity, observedAtUtc))
                .ToArray();
            _snapshot = new BrokerReconciliationSnapshot(
                Account,
                observedAtUtc,
                _snapshot.OpenOrders,
                _snapshot.CompletedOrders,
                Array.AsReadOnly(positions),
                _snapshot.Cash);
            clientOrderId = _lastPositionClientOrderId;
        }
        if (clientOrderId is not { } client || !TryResolve(BrokerOrderQuery.ByClientId(client), out _, out var tracked))
            return;
        EventReceived?.Invoke(new BrokerPositionEvent(
            EventId(tracked!, "position", position.ObservedAtUtc, position.Quantity),
            Account,
            client,
            Utc(position.ObservedAtUtc),
            tracked!.CausationId,
            Instrument,
            position.Quantity));
    }

    private void ProcessOrderError(InteractiveBrokersOrderError error)
    {
        if (_disposed || !TryResolveNative(error.OrderId ?? 0, error.ClientOrderId ?? string.Empty, out var tracked))
            return;
        if (error.ErrorCode == 202)
        {
            PublishTerminal(tracked!.Instruction.Identity.ClientOrderId, VenueEventKind.Cancelled, error.Message, error.ObservedAtUtc);
            return;
        }
        var kind = IsCorrelatedRejection(error.ErrorCode)
            ? VenueEventKind.Rejected
            : VenueEventKind.OutcomeUnknown;
        PublishTerminal(tracked!.Instruction.Identity.ClientOrderId, kind, error.Message, error.ObservedAtUtc);
    }

    private void PublishTerminal(
        ClientOrderId clientOrderId,
        VenueEventKind kind,
        string? reason,
        DateTime? occurredAtUtc = null)
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
        PublishOrderEvent(tracked!, kind, occurredAtUtc ?? UtcNow(), reason: reason);
    }

    private void PublishOrderEvent(
        TrackedOrder tracked,
        VenueEventKind kind,
        DateTime occurredAtUtc,
        CanonicalOrderTerms? replacementTerms = null,
        string? reason = null)
    {
        var utc = Utc(occurredAtUtc);
        var venueEvent = CreateVenueEvent(tracked, kind, utc, replacementTerms: replacementTerms, reason: reason);
        Schedule(() => EventReceived?.Invoke(new BrokerOrderEvent(
            EventId(tracked, kind.ToString(), utc, tracked.FilledQuantity),
            Account,
            tracked.Instruction.Identity.ClientOrderId,
            utc,
            venueEvent)));
    }

    private VenueEvent CreateVenueEvent(
        TrackedOrder tracked,
        VenueEventKind kind,
        DateTime occurredAtUtc,
        FillExecution? fill = null,
        CanonicalOrderTerms? replacementTerms = null,
        string? reason = null)
    {
        var eventId = EventId(tracked, kind.ToString(), occurredAtUtc, tracked.FilledQuantity).Value;
        return new VenueEvent(
            kind,
            tracked.Instruction.Identity.ClientOrderId,
            tracked.BrokerOrderId,
            null,
            fill,
            replacementTerms,
            occurredAtUtc,
            tracked.CausationId,
            new DeduplicationKey(eventId),
            reason);
    }

    private VenueOrderSnapshot BuildVenueSnapshot(InteractiveBrokersOrderSnapshot order)
    {
        if (!string.Equals(order.AccountId, _options.AccountId, StringComparison.Ordinal) || !ContractMatches(order.Contract) ||
            !TryMapTerms(order, out var terms))
            throw new InvalidDataException("An IB reconciliation order could not be mapped to the configured account/contract exactly.");
        var clientOrderId = new ClientOrderId(order.ClientOrderId);
        if (!clientOrderId.IsValid)
            throw new InvalidDataException("An IB reconciliation order lacks a valid stable OrderRef/clientOrderId.");
        var state = MapState(order.Status);
        lock (_gate)
        {
            if (_orders.TryGetValue(clientOrderId, out var tracked))
            {
                RememberNativeIdentity(tracked, order.OrderId);
                tracked.CurrentTerms = terms;
                tracked.State = state;
                tracked.FilledQuantity = order.FilledQuantity;
                tracked.PendingReplacement = null;
                tracked.Acknowledged = state is not OrderLifecycleState.Acknowledging and not OrderLifecycleState.Unknown;
                return ToVenueSnapshot(tracked);
            }
        }

        if (!terms.Quantity.TryGetWholeUnits(out var units))
            throw new InvalidDataException("An IB reconciliation quantity is not a canonical whole quantity.");
        var identity = new OrderIdentity(
            new IntentId($"ib-reconcile-{clientOrderId.Value}"),
            null,
            new LegId(AdapterId),
            clientOrderId,
            NativeBrokerOrderId(order.OrderId),
            null,
            new CorrelationId($"ib-reconcile-{order.OrderId}"),
            new CausationId($"ib-reconcile-{order.OrderId}"),
            new ExecutionLeaseId("ib-reconcile"),
            new FencingToken(1));
        var signedUnits = terms.Side == OrderSide.Buy ? units : checked(-units);
        var intent = new TradeIntent(
            Instrument,
            TradeIntentQuantityMode.Delta,
            ScaledQuantity.FromWhole(signedUnits),
            null,
            null,
            ScaledMoney.Zero,
            "ib-reconciliation",
            0,
            $"ib-{Mode.ToString().ToLowerInvariant()}-v1");
        var instruction = new CanonicalOrderInstruction(identity, intent, terms);
        if (instruction.Validate() != OrderDomainFault.None)
            throw new InvalidDataException("An IB reconciliation instruction is not canonical.");
        var created = new TrackedOrder(
            instruction,
            terms,
            state,
            order.OrderId,
            NativeBrokerOrderId(order.OrderId),
            order.FilledQuantity,
            identity.CausationId)
        {
            Acknowledged = state is not OrderLifecycleState.Acknowledging and not OrderLifecycleState.Unknown,
        };
        lock (_gate)
        {
            if (!_orders.ContainsKey(clientOrderId) && MakeOrderCapacity())
            {
                _orders.Add(clientOrderId, created);
                _nativeToClient[order.OrderId] = clientOrderId;
                _brokerToClient[created.BrokerOrderId] = clientOrderId;
                _insertionOrder.Enqueue(clientOrderId);
            }
        }
        return ToVenueSnapshot(created);
    }

    private bool TryMapRequest(
        CanonicalOrderInstruction instruction,
        out InteractiveBrokersOrderRequest? request,
        out string? reason)
    {
        request = null;
        reason = null;
        if (!TryNativeType(instruction.Terms.OrderType, out var type) ||
            !TryNativeTimeInForce(instruction.Terms.TimeInForce, out var timeInForce))
        {
            reason = "The canonical type or time in force has no exact IB mapping.";
            return false;
        }
        if (!CanRepresentExactlyAsNativeDouble(instruction.Terms.LimitPrice) ||
            !CanRepresentExactlyAsNativeDouble(instruction.Terms.StopPrice))
        {
            reason = "The exact canonical price cannot round-trip through the IB native double API.";
            return false;
        }
        request = new InteractiveBrokersOrderRequest(
            0,
            instruction.Identity.ClientOrderId.Value,
            _options.AccountId,
            Contract,
            instruction.Terms.Side == OrderSide.Buy ? "BUY" : "SELL",
            type,
            timeInForce,
            instruction.Terms.Quantity,
            instruction.Terms.LimitPrice,
            instruction.Terms.StopPrice,
            null,
            null,
            _options.OutsideRegularTradingHours);
        return true;
    }

    private static bool TryMapTerms(InteractiveBrokersOrderSnapshot order, out CanonicalOrderTerms terms)
    {
        terms = default;
        var side = order.Side.ToUpperInvariant() switch
        {
            "BUY" or "BOT" => OrderSide.Buy,
            "SELL" or "SLD" => OrderSide.Sell,
            _ => (OrderSide?)null,
        };
        var type = order.OrderType switch
        {
            InteractiveBrokersNativeOrderType.Market => CanonicalOrderType.Market,
            InteractiveBrokersNativeOrderType.Limit => CanonicalOrderType.Limit,
            InteractiveBrokersNativeOrderType.Stop => CanonicalOrderType.Stop,
            InteractiveBrokersNativeOrderType.StopLimit => CanonicalOrderType.StopLimit,
            _ => (CanonicalOrderType?)null,
        };
        var tif = order.TimeInForce switch
        {
            InteractiveBrokersNativeTimeInForce.Day => CanonicalTimeInForce.Day,
            InteractiveBrokersNativeTimeInForce.GoodTillCancelled => CanonicalTimeInForce.GoodTillCancelled,
            InteractiveBrokersNativeTimeInForce.ImmediateOrCancel => CanonicalTimeInForce.ImmediateOrCancel,
            InteractiveBrokersNativeTimeInForce.FillOrKill => CanonicalTimeInForce.FillOrKill,
            _ => (CanonicalTimeInForce?)null,
        };
        if (!side.HasValue || !type.HasValue || !tif.HasValue)
            return false;
        terms = new CanonicalOrderTerms(
            side.Value,
            type.Value,
            tif.Value,
            order.Quantity,
            order.LimitPrice,
            order.StopPrice);
        return terms.Validate() == OrderDomainFault.None;
    }

    private BrokerExecutionCapabilities DiscoverCanonicalCapabilities(InteractiveBrokersNativeCapabilities native)
    {
        var orderTypes = SupportedOrderTypes.None;
        foreach (var item in native.OrderTypes)
        {
            orderTypes |= item switch
            {
                InteractiveBrokersNativeOrderType.Market => SupportedOrderTypes.Market,
                InteractiveBrokersNativeOrderType.Limit => SupportedOrderTypes.Limit,
                InteractiveBrokersNativeOrderType.Stop => SupportedOrderTypes.Stop,
                InteractiveBrokersNativeOrderType.StopLimit => SupportedOrderTypes.StopLimit,
                _ => SupportedOrderTypes.None,
            };
        }
        var timeInForce = SupportedTimeInForce.None;
        foreach (var item in native.TimeInForce)
        {
            timeInForce |= item switch
            {
                InteractiveBrokersNativeTimeInForce.Day => SupportedTimeInForce.Day,
                InteractiveBrokersNativeTimeInForce.GoodTillCancelled => SupportedTimeInForce.GoodTillCancelled,
                InteractiveBrokersNativeTimeInForce.ImmediateOrCancel => SupportedTimeInForce.ImmediateOrCancel,
                InteractiveBrokersNativeTimeInForce.FillOrKill => SupportedTimeInForce.FillOrKill,
                _ => SupportedTimeInForce.None,
            };
        }
        var tick = native.MinimumPriceIncrement;
        var minimumQuantity = native.MinimumOrderQuantity.TryGetWholeUnits(out var min) && min > 0
            ? ScaledQuantity.FromWhole(min)
            : ScaledQuantity.FromWhole(1);
        var lot = native.QuantityIncrement.TryGetWholeUnits(out var increment) && increment > 0
            ? ScaledQuantity.FromWhole(increment)
            : ScaledQuantity.FromWhole(1);
        return new BrokerExecutionCapabilities(
            $"ib-{Mode.ToString().ToLowerInvariant()}-{native.SelectedAssetClass}-{tick.Coefficient}-{tick.Scale}-v1",
            new VenueCapabilities(orderTypes, timeInForce),
            0,
            minimumQuantity,
            ScaledQuantity.FromWhole(1_000_000_000),
            lot,
            false,
            tick.Scale,
            tick,
            tick,
            null,
            BrokerReplaceSemantics.InPlace,
            false,
            false,
            _options.OutsideRegularTradingHours
                ? native.TradingHoursSchedule
                : native.RegularTradingHours,
            new BrokerRateLimit(_options.MaximumCommandsPerSecond, TimeSpan.FromSeconds(1)));
    }

    private BrokerExecutionCapabilities UnavailableCapabilities() => new(
        $"ib-{Mode.ToString().ToLowerInvariant()}-unavailable-v1",
        new VenueCapabilities(SupportedOrderTypes.None, SupportedTimeInForce.None),
        0,
        ScaledQuantity.FromWhole(1),
        ScaledQuantity.FromWhole(1_000_000_000),
        ScaledQuantity.FromWhole(1),
        false,
        2,
        new ScaledPrice(1, 2),
        new ScaledPrice(1, 2),
        null,
        BrokerReplaceSemantics.InPlace,
        false,
        false,
        BrokerTradingHours.AlwaysOpen,
        new BrokerRateLimit(_options.MaximumCommandsPerSecond, TimeSpan.FromSeconds(1)));

    private static InteractiveBrokersNativeCapabilities UnavailableNativeCapabilities(DateTime observedAtUtc) => new(
        Array.AsReadOnly<InteractiveBrokersNativeOrderType>([]),
        Array.AsReadOnly<InteractiveBrokersNativeTimeInForce>([]),
        Array.AsReadOnly<string>([]),
        string.Empty,
        ScaledQuantity.FromWhole(1),
        ScaledQuantity.FromWhole(1),
        new ScaledPrice(1, 2),
        false,
        BrokerTradingHours.AlwaysOpen,
        BrokerTradingHours.AlwaysOpen,
        string.Empty,
        string.Empty,
        observedAtUtc);

    private void ValidateSession(InteractiveBrokersSessionSnapshot session)
    {
        if (!string.Equals(session.AccountId, _options.AccountId, StringComparison.Ordinal) ||
            session.NextValidOrderId <= 0 || session.ObservedAtUtc.Kind != DateTimeKind.Utc ||
            Mode == ExecutionMode.Paper && !session.IsPaper ||
            Mode == ExecutionMode.Live && session.IsPaper)
        {
            throw new InvalidDataException("The authenticated IB account identity/environment does not match the gated endpoint.");
        }
    }

    private void ValidateNativeCapabilities(InteractiveBrokersNativeCapabilities capabilities)
    {
        if (capabilities.ObservedAtUtc.Kind != DateTimeKind.Utc ||
            capabilities.OrderTypes.Count == 0 || capabilities.TimeInForce.Count == 0 ||
            capabilities.AssetClasses.Count == 0 || string.IsNullOrWhiteSpace(capabilities.SelectedAssetClass) ||
            !capabilities.MinimumOrderQuantity.IsValid || capabilities.MinimumOrderQuantity.Coefficient <= 0 ||
            !capabilities.QuantityIncrement.IsValid || capabilities.QuantityIncrement.Coefficient <= 0 ||
            !capabilities.MinimumPriceIncrement.IsValid || capabilities.MinimumPriceIncrement.Coefficient <= 0 ||
            !capabilities.TradingHoursSchedule.IsValid ||
            !capabilities.RegularTradingHours.IsValid)
        {
            throw new InvalidDataException("IB returned an invalid or incomplete native capability snapshot.");
        }
        if (_options.OutsideRegularTradingHours && !capabilities.SupportsOutsideRegularTradingHours)
        {
            throw new InvalidDataException(
                "The configured IB contract does not advertise execution outside regular trading hours.");
        }
    }

    private bool TryAuthorizeCommand(BrokerSubmitCommand command)
    {
        if (!TryValidateLiveAuthorization(out var reason))
        {
            CloseUnauthorizedSession();
            return false;
        }
        return Mode != ExecutionMode.Live || ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command);
    }

    private bool TryAuthorizeCommand(BrokerCancelCommand command)
    {
        if (!TryValidateLiveAuthorization(out var reason))
        {
            CloseUnauthorizedSession();
            return false;
        }
        return Mode != ExecutionMode.Live || ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command);
    }

    private bool TryAuthorizeCommand(BrokerReplaceCommand command)
    {
        if (!TryValidateLiveAuthorization(out var reason))
        {
            CloseUnauthorizedSession();
            return false;
        }
        return Mode != ExecutionMode.Live || ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command);
    }

    private bool TryValidateLiveAuthorization(out string? reason)
    {
        reason = null;
        if (Mode == ExecutionMode.Paper)
            return true;
        try
        {
            var endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(_options, _liveConfirmationStore);
            if (endpoint != _endpoint || !endpoint.IsLive)
                throw new InvalidOperationException("The IB LIVE endpoint no longer matches its gated endpoint token.");
            return true;
        }
        catch (Exception exception)
        {
            reason = $"IB LIVE authorization is absent or was revoked: {SafeReason(exception)}";
            return false;
        }
    }

    private void CloseUnauthorizedSession()
    {
        CancellationTokenSource? connectionLifetime;
        lock (_gate)
            connectionLifetime = _connectionLifetime;
        try
        {
            connectionLifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
    }

    private bool TryResolve(BrokerOrderQuery query, out ClientOrderId clientOrderId, out TrackedOrder? tracked)
    {
        lock (_gate)
        {
            if (query.ClientOrderId is { } client && _orders.TryGetValue(client, out tracked))
            {
                clientOrderId = client;
                return true;
            }
            if (query.BrokerOrderId is { } broker && _brokerToClient.TryGetValue(broker, out clientOrderId) &&
                _orders.TryGetValue(clientOrderId, out tracked))
                return true;
        }
        clientOrderId = default;
        tracked = null;
        return false;
    }

    private bool TryResolveNative(int orderId, string clientOrderIdValue, out TrackedOrder? tracked)
    {
        lock (_gate)
            return TryResolveNativeLocked(orderId, clientOrderIdValue, out tracked);
    }

    private bool TryResolveNativeLocked(int orderId, string clientOrderIdValue, out TrackedOrder? tracked)
    {
        if (orderId > 0 && _nativeToClient.TryGetValue(orderId, out var client) && _orders.TryGetValue(client, out tracked))
        {
            if (!string.IsNullOrEmpty(clientOrderIdValue))
            {
                var suppliedClient = new ClientOrderId(clientOrderIdValue);
                if (!suppliedClient.IsValid || suppliedClient != client)
                {
                    tracked = null;
                    return false;
                }
            }
            return true;
        }
        var supplied = new ClientOrderId(clientOrderIdValue);
        if (supplied.IsValid && _orders.TryGetValue(supplied, out tracked))
        {
            if (orderId > 0 && tracked.NativeOrderId != orderId)
            {
                tracked = null;
                return false;
            }
            return true;
        }
        tracked = null;
        return false;
    }

    private void RememberNativeIdentity(TrackedOrder tracked, int nativeOrderId)
    {
        if (nativeOrderId <= 0)
            throw new InvalidDataException("IB returned an invalid native order ID.");
        var client = tracked.Instruction.Identity.ClientOrderId;
        if (_nativeToClient.TryGetValue(nativeOrderId, out var existing) && existing != client)
            throw new InvalidDataException("IB reused one native order ID for different client orders.");
        if (tracked.NativeOrderId != nativeOrderId)
        {
            _nativeToClient.Remove(tracked.NativeOrderId);
            _brokerToClient.Remove(tracked.BrokerOrderId);
            tracked.NativeOrderId = nativeOrderId;
            tracked.BrokerOrderId = NativeBrokerOrderId(nativeOrderId);
        }
        _nativeToClient[nativeOrderId] = client;
        _brokerToClient[tracked.BrokerOrderId] = client;
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
            _nativeToClient.Remove(tracked.NativeOrderId);
            _brokerToClient.Remove(tracked.BrokerOrderId);
        }
        return _orders.Count < _options.MaximumTrackedOrders;
    }

    private bool TryGetConnectionToken(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_connectionLifetime is not null && !_connectionLifetime.IsCancellationRequested)
            {
                token = _connectionLifetime.Token;
                return true;
            }
        }
        token = new CancellationToken(canceled: true);
        return false;
    }

    private bool TryConsumeRateBudget()
    {
        lock (_gate)
        {
            var now = UtcNow();
            if (now < _rateWindowStartedUtc || now - _rateWindowStartedUtc >= TimeSpan.FromSeconds(1))
            {
                _rateWindowStartedUtc = now;
                _commandsInRateWindow = 0;
            }
            if (_commandsInRateWindow >= _options.MaximumCommandsPerSecond)
                return false;
            _commandsInRateWindow++;
            return true;
        }
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
            SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, Session.IsExecutionAuthenticated, false);
        }
    }

    private void SetSession(
        ExecutionSessionHealth health,
        bool isDataConnected,
        bool isExecutionAuthenticated,
        bool isExecutionCertified)
    {
        lock (_gate)
        {
            _session = new BrokerExecutionSession(
                Account,
                health,
                isDataConnected,
                isExecutionAuthenticated,
                isExecutionCertified,
                UtcNow());
        }
    }

    private void Unsubscribe()
    {
        _transport.OrderUpdated -= OnOrderUpdated;
        _transport.ExecutionReceived -= OnExecutionReceived;
        _transport.CommissionReceived -= OnCommissionReceived;
        _transport.PositionUpdated -= OnPositionUpdated;
        _transport.OrderError -= OnOrderError;
        _transport.Faulted -= OnTransportFaulted;
        if (_ownedScheduler is not null)
            _ownedScheduler.CallbackFaulted -= OnSchedulerFaulted;
    }

    private async Task SafeDisconnectTransportAsync()
    {
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void RememberPublishedExecution(string executionId)
    {
        while (_publishedExecutions.Count >= _options.MaximumTrackedOrders * 4 &&
               _publishedExecutionOrder.TryDequeue(out var oldest))
            _publishedExecutions.Remove(oldest);
        _publishedExecutions.Add(executionId);
        _publishedExecutionOrder.Enqueue(executionId);
    }

    private BrokerDispatchReceipt CreateReceipt(
        BrokerAdapterCommandKind kind,
        ClientOrderId clientOrderId,
        CausationId causationId)
    {
        var material = $"{AdapterId}|{Account.AccountId.Value}|{kind}|{clientOrderId.Value}|{causationId.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerDispatchReceipt(
            new DispatchReceiptId($"ib-{Convert.ToHexString(hash).ToLowerInvariant()}"),
            Account,
            kind,
            clientOrderId,
            causationId,
            UtcNow());
    }

    private BrokerAdapterEventId EventId(TrackedOrder tracked, string kind, DateTime occurredAtUtc, ScaledQuantity quantity)
    {
        var material = $"{AdapterId}|{tracked.Instruction.Identity.ClientOrderId.Value}|{tracked.NativeOrderId}|{kind}|{Utc(occurredAtUtc).Ticks}|{quantity.Coefficient}:{quantity.Scale}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerAdapterEventId($"ib-event-{Convert.ToHexString(hash).ToLowerInvariant()}");
    }

    private static BrokerAdapterCommandResult Dispatched(BrokerDispatchReceipt receipt) => new(
        BrokerAdapterCommandStatus.Dispatched,
        BrokerAdapterCommandFault.None,
        receipt,
        0,
        null);

    private static BrokerAdapterCommandResult Rejected(BrokerAdapterCommandFault fault, string reason) => new(
        BrokerAdapterCommandStatus.RejectedBeforeDispatch,
        fault,
        null,
        0,
        reason);

    private static BrokerAdapterCommandResult AdmissionRejected(ExecutionAdmissionResult admission) =>
        Rejected(
            admission.Fault is ExecutionAdmissionFault.DataDisconnected or
                ExecutionAdmissionFault.ExecutionNotAuthenticated or
                ExecutionAdmissionFault.ExecutionNotCertified or
                ExecutionAdmissionFault.SessionUnavailable or
                ExecutionAdmissionFault.InvalidSession
                ? BrokerAdapterCommandFault.ExecutionUnavailable
                : BrokerAdapterCommandFault.UnsupportedCapability,
            admission.Reason ?? admission.Fault.ToString());

    private static VenueOrderSnapshot ToVenueSnapshot(TrackedOrder tracked) => new(
        tracked.Instruction,
        tracked.CurrentTerms,
        tracked.State,
        tracked.BrokerOrderId,
        null,
        tracked.FilledQuantity);

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

    private bool ContractMatches(InteractiveBrokersContract contract) =>
        contract is not null &&
        (_options.ContractId <= 0 || contract.ContractId == _options.ContractId) &&
        string.Equals(contract.Symbol, Contract.Symbol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(contract.SecurityType, Contract.SecurityType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(contract.Currency, Contract.Currency, StringComparison.OrdinalIgnoreCase);

    private static OrderLifecycleState MapState(InteractiveBrokersNativeOrderStatus status) => status switch
    {
        InteractiveBrokersNativeOrderStatus.PendingSubmit => OrderLifecycleState.Acknowledging,
        InteractiveBrokersNativeOrderStatus.PreSubmitted or InteractiveBrokersNativeOrderStatus.Submitted => OrderLifecycleState.Working,
        InteractiveBrokersNativeOrderStatus.PendingCancel => OrderLifecycleState.PendingCancel,
        InteractiveBrokersNativeOrderStatus.Cancelled or InteractiveBrokersNativeOrderStatus.ApiCancelled => OrderLifecycleState.Cancelled,
        InteractiveBrokersNativeOrderStatus.Filled => OrderLifecycleState.Filled,
        InteractiveBrokersNativeOrderStatus.Inactive or InteractiveBrokersNativeOrderStatus.Rejected => OrderLifecycleState.Rejected,
        _ => OrderLifecycleState.Unknown,
    };

    private static bool TryNativeType(CanonicalOrderType type, out InteractiveBrokersNativeOrderType native)
    {
        native = type switch
        {
            CanonicalOrderType.Market => InteractiveBrokersNativeOrderType.Market,
            CanonicalOrderType.Limit => InteractiveBrokersNativeOrderType.Limit,
            CanonicalOrderType.Stop => InteractiveBrokersNativeOrderType.Stop,
            CanonicalOrderType.StopLimit => InteractiveBrokersNativeOrderType.StopLimit,
            _ => default,
        };
        return Enum.IsDefined(type);
    }

    private static bool TryNativeTimeInForce(CanonicalTimeInForce timeInForce, out InteractiveBrokersNativeTimeInForce native)
    {
        native = timeInForce switch
        {
            CanonicalTimeInForce.Day => InteractiveBrokersNativeTimeInForce.Day,
            CanonicalTimeInForce.GoodTillCancelled => InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
            CanonicalTimeInForce.ImmediateOrCancel => InteractiveBrokersNativeTimeInForce.ImmediateOrCancel,
            CanonicalTimeInForce.FillOrKill => InteractiveBrokersNativeTimeInForce.FillOrKill,
            _ => default,
        };
        return Enum.IsDefined(timeInForce);
    }

    private static bool TryAdd(ScaledQuantity left, ScaledQuantity right, out ScaledQuantity result)
    {
        result = default;
        if (!ScaledValueMath.TryAdd(
                left.Coefficient,
                left.Scale,
                right.Coefficient,
                right.Scale,
                out var coefficient,
                out var scale) ||
            !ScaledValueMath.TryNarrow(coefficient, scale, out var narrowed, out var narrowedScale))
            return false;
        result = new ScaledQuantity(narrowed, narrowedScale);
        return true;
    }

    private static bool CanRepresentExactlyAsNativeDouble(ScaledPrice? value)
    {
        if (!value.HasValue)
            return true;
        decimal divisor = 1;
        for (var index = 0; index < value.Value.Scale; index++)
            divisor *= 10;
        var exact = value.Value.Coefficient / divisor;
        var native = (double)exact;
        return double.IsFinite(native) && (decimal)native == exact;
    }

    private static BrokerOrderId NativeBrokerOrderId(int nativeOrderId) =>
        new(nativeOrderId.ToString(CultureInfo.InvariantCulture));

    private static bool IsCorrelatedRejection(int code) => code is
        103 or 107 or 109 or 110 or 111 or 201 or 321 or 355 or 382 or 383 or 387 or 399 or 10148;

    private DateTime UtcNow() => Utc(_clock.UtcNow);

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string SafeReason(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 512 ? message : message[..512];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TrackedOrder(
        CanonicalOrderInstruction instruction,
        CanonicalOrderTerms currentTerms,
        OrderLifecycleState state,
        int nativeOrderId,
        BrokerOrderId brokerOrderId,
        ScaledQuantity filledQuantity,
        CausationId causationId)
    {
        internal CanonicalOrderInstruction Instruction { get; } = instruction;
        internal CanonicalOrderTerms CurrentTerms { get; set; } = currentTerms;
        internal OrderLifecycleState State { get; set; } = state;
        internal int NativeOrderId { get; set; } = nativeOrderId;
        internal BrokerOrderId BrokerOrderId { get; set; } = brokerOrderId;
        internal ScaledQuantity FilledQuantity { get; set; } = filledQuantity;
        internal CausationId CausationId { get; set; } = causationId;
        internal CanonicalOrderTerms? PendingReplacement { get; set; }
        internal bool Acknowledged { get; set; }
    }
}
