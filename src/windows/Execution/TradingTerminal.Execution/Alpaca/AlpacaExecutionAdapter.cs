using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Alpaca;

public sealed record AlpacaNativeCapabilities(
    IReadOnlyList<string> OrderTypes,
    IReadOnlyList<string> TimeInForce,
    IReadOnlyList<string> AssetClasses,
    string SelectedAssetClass,
    bool SupportsFractionalQuantity,
    bool SupportsNotionalOrders,
    ScaledQuantity? MinimumOrderSize,
    ScaledQuantity? QuantityIncrement,
    ScaledPrice? PriceIncrement);

/// <summary>
/// Alpaca Trading API v2 adapter. It is bound to one configured symbol and the exact
/// gated paper/live endpoint. REST polling is injected and is the production trade-update mechanism.
/// </summary>
public sealed class AlpacaExecutionAdapter : IBrokerExecutionAdapter, IDisposable, IAsyncDisposable
{
    public const string StableAdapterId = "alpaca-paper";
    public const string LiveAdapterId = "alpaca-live";
    private const int MaximumPendingOperations = 512;

    private readonly object _gate = new();
    private readonly AlpacaExecutionOptions _options;
    private readonly AlpacaExecutionEndpoint _endpoint;
    private readonly ILiveExecutionConfirmationStore? _liveConfirmationStore;
    private readonly IAlpacaExecutionTransport _transport;
    private readonly IAlpacaTradeUpdateSource _updateSource;
    private readonly IClock _clock;
    private readonly IAdapterEventScheduler _scheduler;
    private readonly AlpacaSerializedEventScheduler? _ownedScheduler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ClientOrderId, TrackedOrder> _orders = [];
    private readonly Dictionary<BrokerOrderId, ClientOrderId> _brokerToClient = [];
    private readonly Queue<ClientOrderId> _insertionOrder = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private CancellationTokenSource? _connectionLifetime;
    private BrokerExecutionSession _session;
    private BrokerExecutionCapabilities _capabilities;
    private BrokerReconciliationSnapshot _snapshot;
    private DateTime _rateWindowStartedUtc;
    private int _commandsInRateWindow;
    private int _pendingOperationSlots;
    private bool _disposed;
    private ScaledQuantity _inferredPosition = ScaledQuantity.Zero;

    public AlpacaExecutionAdapter(
        AlpacaExecutionOptions options,
        IAlpacaExecutionTransport transport,
        IAlpacaTradeUpdateSource updateSource,
        IClock clock,
        IAdapterEventScheduler scheduler,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        if (!_options.Enabled)
            throw new InvalidOperationException("Alpaca execution must be explicitly enabled before constructing the adapter.");
        _liveConfirmationStore = confirmationStore;
        _endpoint = AlpacaExecutionEndpointGate.Resolve(_options, confirmationStore);
        var configurationFault = _options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            throw new InvalidOperationException(configurationFault);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (_transport.Endpoint != _endpoint || !_transport.Endpoint.IsAuthorized)
            throw new InvalidOperationException("The injected Alpaca transport does not identify the exact gated endpoint.");
        _updateSource = updateSource ?? throw new ArgumentNullException(nameof(updateSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _ownedScheduler = scheduler as AlpacaSerializedEventScheduler;

        Symbol = _options.Symbol.Trim().ToUpperInvariant();
        Instrument = new InstrumentId(_options.CanonicalInstrumentId);
        Account = new BrokerExecutionAccount(
            new ExecutionAdapterId(AdapterId),
            new BrokerAccountId(string.IsNullOrWhiteSpace(_options.ExpectedAccountId)
                ? $"alpaca-{EnvironmentLabel.ToLowerInvariant()}-account"
                : _options.ExpectedAccountId));
        var now = UtcNow();
        _session = new BrokerExecutionSession(Account, ExecutionSessionHealth.Disconnected, false, false, false, now);
        _capabilities = UnavailableCapabilities();
        NativeCapabilities = UnavailableNativeCapabilities();
        _snapshot = EmptySnapshot(DateTime.UnixEpoch);
        _rateWindowStartedUtc = now;

        _updateSource.OrderUpdated += OnOrderUpdated;
        _updateSource.Faulted += OnUpdateSourceFaulted;
        if (_ownedScheduler is not null)
            _ownedScheduler.CallbackFaulted += OnSchedulerFaulted;
    }

    public string BrokerId => AlpacaExecutionOptions.BrokerId;

    public ExecutionMode Mode => _endpoint.Mode;

    public string AdapterId => Mode == ExecutionMode.Live ? LiveAdapterId : StableAdapterId;

    private string EnvironmentLabel => Mode == ExecutionMode.Live ? "LIVE" : "PAPER";

    public InstrumentId Instrument { get; }

    public string Symbol { get; }

    public string? NativeAccountId { get; private set; }

    public ScaledPrice? LatestReferencePrice { get; private set; }

    public DateTime? LatestReferencePriceObservedAtUtc { get; private set; }

    public DateTime? LatestReferencePriceFetchedAtUtc { get; private set; }

    public AlpacaNativeCapabilities NativeCapabilities { get; private set; }

    public bool IsDataOnly => Session.IsDataConnected && !Session.IsExecutionAuthenticated;

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

    /// <summary>Cash evidence is separate because the shared broker event vocabulary has no cash event.</summary>
    public event Action<BrokerCashSnapshot>? CashSnapshotReceived;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(_options.KeyId, _options.SecretKey, cancellationToken);

    public async Task ConnectAsync(
        string keyId,
        string secretKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateLiveAuthorization(out var authorizationFault))
        {
            CloseUnauthorizedSession();
            throw new InvalidOperationException(authorizationFault);
        }
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException($"Both Alpaca {EnvironmentLabel} credentials are required.");
        if (Mode == ExecutionMode.Live &&
            (!string.Equals(keyId, _options.KeyId, StringComparison.Ordinal) ||
             !string.Equals(secretKey, _options.SecretKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The runtime Alpaca LIVE credentials do not match the credentials that passed the live endpoint gate.");
        }

        var priorNativeAccountId = NativeAccountId;
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource connectionLifetime;
        lock (_gate)
        {
            _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            connectionLifetime = _connectionLifetime;
        }
        using var connectionAttempt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connectionLifetime.Token);
        var connectionToken = connectionAttempt.Token;
        try
        {
            await _transport.ConnectAsync(keyId, secretKey, connectionToken).ConfigureAwait(false);
            SetSession(ExecutionSessionHealth.Degraded, true, false, false);

            var account = await _transport.GetAccountAsync(connectionToken).ConfigureAwait(false);
            if (Mode == ExecutionMode.Live &&
                !string.Equals(account.AccountId, _options.ExpectedAccountId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The authenticated Alpaca LIVE account does not match the exact persisted confirmation binding.");
            }
            var asset = await _transport.GetAssetAsync(Symbol, connectionToken).ConfigureAwait(false);
            if (!string.Equals(asset.Symbol, Symbol, StringComparison.Ordinal) ||
                !asset.Tradable ||
                asset.AssetClass is not ("us_equity" or "crypto"))
            {
                throw new InvalidDataException("The configured Alpaca asset is absent, non-tradable, or unsupported.");
            }

            if (priorNativeAccountId is not null &&
                !string.Equals(priorNativeAccountId, account.AccountId, StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    if (_orders.Count != 0)
                    {
                        throw new InvalidOperationException(
                            $"The authenticated Alpaca {EnvironmentLabel} account changed while tracked order state still exists; create a new adapter binding.");
                    }
                    ClearTrackedState();
                }
            }
            NativeAccountId = account.AccountId;
            NativeCapabilities = DiscoverNativeCapabilities(asset);
            var canCertifyExactExecution =
                account.IsExecutionAuthorized &&
                string.Equals(asset.AssetClass, "us_equity", StringComparison.Ordinal);
            lock (_gate)
                _capabilities = DiscoverCanonicalCapabilities(asset);
            SetSession(
                ExecutionSessionHealth.Degraded,
                isDataConnected: true,
                isExecutionAuthenticated: account.IsExecutionAuthorized,
                isExecutionCertified: false);

            await RefreshReconciliationAsync(connectionToken).ConfigureAwait(false);
            try
            {
                await RefreshReferencePriceAsync(connectionToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LatestReferencePrice = null;
                LatestReferencePriceObservedAtUtc = null;
                LatestReferencePriceFetchedAtUtc = null;
            }

            SetSession(
                ExecutionSessionHealth.Healthy,
                isDataConnected: true,
                isExecutionAuthenticated: account.IsExecutionAuthorized,
                isExecutionCertified: canCertifyExactExecution);
            await _updateSource.StartAsync(_transport, connectionToken).ConfigureAwait(false);
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
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
            NativeAccountId = priorNativeAccountId;
            LatestReferencePrice = null;
            LatestReferencePriceObservedAtUtc = null;
            LatestReferencePriceFetchedAtUtc = null;
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
            await _updateSource.StopAsync(cancellationToken).ConfigureAwait(false);
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connectionLifetime?.Dispose();
            LatestReferencePrice = null;
            LatestReferencePriceObservedAtUtc = null;
            LatestReferencePriceFetchedAtUtc = null;
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        }
    }

    public async Task RefreshReferencePriceAsync(CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
            throw new InvalidOperationException($"The Alpaca {EnvironmentLabel} transport is disconnected.");
        LatestReferencePrice = null;
        LatestReferencePriceObservedAtUtc = null;
        LatestReferencePriceFetchedAtUtc = null;
        var latest = await _transport.GetLatestTradeAsync(Symbol, cancellationToken).ConfigureAwait(false);
        if (latest is null ||
            !latest.Price.IsValid ||
            latest.Price.Coefficient <= 0 ||
            latest.TimestampUtc.Kind != DateTimeKind.Utc)
        {
            LatestReferencePrice = null;
            LatestReferencePriceObservedAtUtc = null;
            return;
        }
        LatestReferencePrice = latest.Price;
        LatestReferencePriceObservedAtUtc = latest.TimestampUtc;
        LatestReferencePriceFetchedAtUtc = UtcNow();
    }

    public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command)
    {
        if (command is null || command.Instruction is null || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The Alpaca submit command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "Alpaca LIVE submit requires a current one-use OMS guardrail admission.");
        }
        if (command.Instruction.TradeIntent.Instrument != Instrument)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "This Alpaca adapter is certified for one configured instrument only.");
        if (command.Instruction.Identity.ClientOrderId.Value.Length > 48)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "Alpaca client_order_id is limited to 48 characters.");

        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, command.Instruction, UtcNow());
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local Alpaca command budget is exhausted.");
        if (!TryMapSubmit(command.Instruction, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);

        var clientOrderId = command.Instruction.Identity.ClientOrderId;
        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The Alpaca {EnvironmentLabel} connection lifetime has ended.");
        lock (_gate)
        {
            if (_orders.ContainsKey(clientOrderId))
            {
                return new BrokerAdapterCommandResult(
                    BrokerAdapterCommandStatus.Conflict,
                    BrokerAdapterCommandFault.Conflict,
                    null,
                    0,
                    "The client order ID is already bound to an Alpaca order.");
            }
            if (!MakeOrderCapacity())
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded Alpaca order-correlation table is full.");
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded Alpaca command-operation table is full.");
            _pendingOperationSlots++;
            _orders.Add(clientOrderId, new TrackedOrder(
                command.Instruction,
                command.Instruction.Terms,
                OrderLifecycleState.Acknowledging,
                null,
                ScaledQuantity.Zero,
                null,
                command.CausationId));
            _insertionOrder.Enqueue(clientOrderId);
        }

        TrackOperation(SubmitAsync(clientOrderId, request!, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Submit, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid)
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The Alpaca cancel command is invalid.");
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "Alpaca LIVE cancel requires a current one-use OMS guardrail admission.");
        }
        if (!Session.CanExecute)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The Alpaca {EnvironmentLabel} session cannot execute.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) || tracked!.BrokerOrderId is not { } brokerOrderId)
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching Alpaca broker order is known.");
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local Alpaca command budget is exhausted.");

        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The Alpaca {EnvironmentLabel} connection lifetime has ended.");
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded Alpaca command-operation table is full.");
            _pendingOperationSlots++;
            tracked.State = OrderLifecycleState.PendingCancel;
            tracked.CausationId = command.CausationId;
        }
        TrackOperation(CancelAsync(clientOrderId, brokerOrderId, connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Cancel, clientOrderId, command.CausationId));
    }

    public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The Alpaca replace command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "Alpaca LIVE replace requires a current one-use OMS guardrail admission.");
        }
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) || tracked!.BrokerOrderId is not { } brokerOrderId)
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "No matching Alpaca broker order is known.");
        if (command.ReplacementTerms.Side != tracked.CurrentTerms.Side ||
            command.ReplacementTerms.OrderType != tracked.CurrentTerms.OrderType)
        {
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "Alpaca replacement cannot change side or order type.");
        }

        var replacement = tracked.Instruction with { Terms = command.ReplacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, replacement, UtcNow(), isReplace: true);
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local Alpaca command budget is exhausted.");
        if (!TryMapReplace(command.ReplacementTerms, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);

        if (!TryGetConnectionToken(out var connectionToken))
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, $"The Alpaca {EnvironmentLabel} connection lifetime has ended.");
        lock (_gate)
        {
            if (_pendingOperationSlots >= MaximumPendingOperations)
                return Rejected(BrokerAdapterCommandFault.RateLimited, "The bounded Alpaca command-operation table is full.");
            _pendingOperationSlots++;
            tracked.State = OrderLifecycleState.PendingReplace;
            tracked.CausationId = command.CausationId;
            tracked.PendingReplacement = command.ReplacementTerms;
        }
        TrackOperation(ReplaceAsync(
            clientOrderId,
            brokerOrderId,
            request!,
            command.ReplacementTerms,
            connectionToken));
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId));
    }

    public BrokerOrderQueryResult Query(BrokerOrderQuery query)
    {
        // The synchronous seam is cache-only by design: RefreshReconciliationAsync hydrates exact
        // client/broker correlations so no query performs HTTP on the coordinator caller thread.
        if (!query.IsValid)
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.InvalidCommand, null, "The Alpaca query is invalid.");
        if (!TryResolve(query, out _, out var tracked))
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.OrderNotFound, null);
        lock (_gate)
            return new BrokerOrderQueryResult(true, BrokerAdapterCommandFault.None, ToVenueSnapshot(tracked!));
    }

    /// <summary>
    /// Explicit asynchronous cache recovery for a caller that knows one native identity. The
    /// synchronous coordinator seam remains network-free; successful recovery hydrates both client
    /// and broker lookup maps for later Query/Cancel/Replace calls.
    /// </summary>
    public async Task<bool> RefreshOrderCorrelationAsync(
        BrokerOrderQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid || !_transport.IsConnected)
            return false;
        AlpacaOrderSnapshot? order = query.ClientOrderId is { } clientOrderId
            ? await _transport.GetOrderByClientIdAsync(clientOrderId.Value, cancellationToken).ConfigureAwait(false)
            : await _transport.GetOrderByIdAsync(query.BrokerOrderId!.Value.Value, cancellationToken).ConfigureAwait(false);
        if (order is null || !string.Equals(order.Symbol, Symbol, StringComparison.Ordinal))
            return false;
        if (query.ClientOrderId is { } expectedClient &&
            !string.Equals(order.ClientOrderId, expectedClient.Value, StringComparison.Ordinal) ||
            query.BrokerOrderId is { } expectedBroker &&
            !string.Equals(order.OrderId, expectedBroker.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Alpaca order lookup response did not match its requested identity.");
        }
        if (!TryBuildVenueSnapshot(order, out var snapshot) || snapshot is null)
            throw new InvalidDataException("The recovered Alpaca order correlation could not be represented exactly.");
        MergeSnapshotOrder(snapshot);
        return true;
    }

    public BrokerReconciliationSnapshot CaptureReconciliationSnapshot()
    {
        lock (_gate)
            return CopySnapshot(_snapshot);
    }

    public async Task RefreshReconciliationAsync(CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
            throw new InvalidOperationException($"The Alpaca {EnvironmentLabel} transport is disconnected.");

        var openTask = _transport.GetOrdersAsync(AlpacaOrderStatusFilter.Open, cancellationToken);
        var closedTask = _transport.GetOrdersAsync(AlpacaOrderStatusFilter.Closed, cancellationToken);
        var positionsTask = _transport.GetPositionsAsync(cancellationToken);
        var accountTask = _transport.GetAccountAsync(cancellationToken);
        await Task.WhenAll(openTask, closedTask, positionsTask, accountTask).ConfigureAwait(false);

        var now = UtcNow();
        var openOrders = await openTask.ConfigureAwait(false);
        var closedOrders = await closedTask.ConfigureAwait(false);
        if (openOrders.Count >= 500 || closedOrders.Count >= 500)
        {
            throw new InvalidDataException(
                "A bounded Alpaca reconciliation order page reached its 500-order limit and may be incomplete.");
        }
        var account = await accountTask.ConfigureAwait(false);
        if (!string.Equals(account.AccountId, NativeAccountId, StringComparison.Ordinal))
            throw new InvalidDataException("The Alpaca reconciliation response belonged to another account.");
        var positions = (await positionsTask.ConfigureAwait(false))
            .Where(item => string.Equals(item.Symbol, Symbol, StringComparison.Ordinal))
            .Select(item => new BrokerPositionSnapshot(Instrument, item.Quantity, item.ObservedAtUtc.Kind == DateTimeKind.Utc ? item.ObservedAtUtc : now))
            .ToArray();
        if (positions.Length > 1)
            throw new InvalidDataException("Alpaca returned duplicate positions for the configured symbol.");
        var authoritativeOrders = SelectAuthoritativeOrders(openOrders.Concat(closedOrders));
        var open = BuildOrderSnapshots(authoritativeOrders.Where(static item => IsOpenStatus(item.Status)).ToArray());
        var completed = BuildOrderSnapshots(authoritativeOrders.Where(static item => !IsOpenStatus(item.Status)).ToArray());
        var cash = new BrokerCashSnapshot(account.Currency, account.Cash, account.BuyingPower, now);
        var snapshot = new BrokerReconciliationSnapshot(
            Account,
            now,
            Array.AsReadOnly(open),
            Array.AsReadOnly(completed),
            Array.AsReadOnly(positions),
            Array.AsReadOnly([cash]));
        lock (_gate)
        {
            _snapshot = snapshot;
            _inferredPosition = positions.Length == 1 ? positions[0].Quantity : ScaledQuantity.Zero;
        }
        Schedule(() => CashSnapshotReceived?.Invoke(cash));
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
            if (_updateSource is IDisposable disposableSource)
                disposableSource.Dispose();
            else
                _updateSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AwaitPendingOperationsAsync().GetAwaiter().GetResult();
            if (_transport is IDisposable disposableTransport)
                disposableTransport.Dispose();
            else
                _transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            await _updateSource.DisposeAsync().ConfigureAwait(false);
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

    private async Task SubmitAsync(ClientOrderId clientOrderId, AlpacaSubmitRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var order = await _transport.SubmitOrderAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            OnOrderUpdated(order);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AlpacaApiException exception) when ((int)exception.StatusCode is >= 400 and < 500)
        {
            PublishTerminal(clientOrderId, VenueEventKind.Rejected, SafeReason(exception));
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private async Task CancelAsync(ClientOrderId clientOrderId, BrokerOrderId brokerOrderId, CancellationToken cancellationToken)
    {
        try
        {
            await _transport.CancelOrderAsync(brokerOrderId.Value, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AlpacaApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            PublishTerminal(
                clientOrderId,
                VenueEventKind.OutcomeUnknown,
                "Alpaca returned 404 for cancellation; the original order outcome requires reconciliation.");
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private async Task ReplaceAsync(
        ClientOrderId clientOrderId,
        BrokerOrderId brokerOrderId,
        AlpacaReplaceRequest request,
        CanonicalOrderTerms replacementTerms,
        CancellationToken cancellationToken)
    {
        try
        {
            var order = await _transport.ReplaceOrderAsync(brokerOrderId.Value, request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            TrackedOrder? tracked;
            var publishReplacement = false;
            lock (_gate)
            {
                if (!_orders.TryGetValue(clientOrderId, out tracked))
                    return;
                RememberBrokerId(tracked, order.OrderId);
                if (tracked.PendingReplacement is not null)
                {
                    tracked.CurrentTerms = replacementTerms;
                    tracked.PendingReplacement = null;
                    tracked.State = tracked.FilledQuantity.Coefficient == 0
                        ? OrderLifecycleState.Working
                        : OrderLifecycleState.PartiallyFilled;
                    publishReplacement = true;
                }
            }
            if (publishReplacement)
            {
                PublishOrderEvent(
                    tracked!,
                    VenueEventKind.Replaced,
                    order.UpdatedAtUtc,
                    replacementTerms: replacementTerms);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AlpacaApiException exception) when ((int)exception.StatusCode is >= 400 and < 500)
        {
            PublishTerminal(
                clientOrderId,
                VenueEventKind.OutcomeUnknown,
                $"Alpaca replacement was not confirmed and the original order requires reconciliation: {SafeReason(exception)}");
        }
        catch (Exception exception)
        {
            PublishTerminal(clientOrderId, VenueEventKind.OutcomeUnknown, SafeReason(exception));
        }
    }

    private void OnOrderUpdated(AlpacaOrderSnapshot update)
    {
        if (_disposed || !string.Equals(update.Symbol, Symbol, StringComparison.Ordinal))
            return;
        var clientOrderId = new ClientOrderId(update.ClientOrderId);
        var status = update.Status.ToLowerInvariant();
        TrackedOrder? tracked;
        lock (_gate)
        {
            if (!_orders.TryGetValue(clientOrderId, out tracked))
                return;
            if (status != "replaced" || tracked.BrokerOrderId is null ||
                string.Equals(tracked.BrokerOrderId.Value.Value, update.OrderId, StringComparison.Ordinal))
            {
                RememberBrokerId(tracked, update.OrderId);
            }
        }

        if (status is "new" or "accepted" or "pending_new" or "accepted_for_bidding")
        {
            var publish = false;
            lock (_gate)
            {
                if (!tracked.Acknowledged)
                {
                    tracked.Acknowledged = true;
                    tracked.State = OrderLifecycleState.Working;
                    publish = true;
                }
            }
            if (publish)
                PublishOrderEvent(tracked, VenueEventKind.Acknowledged, update.UpdatedAtUtc);
            return;
        }

        if (status is "partially_filled" or "filled")
        {
            PublishFillDelta(tracked, update);
            return;
        }

        if (status == "replaced")
        {
            CanonicalOrderTerms? replacementTerms;
            lock (_gate)
            {
                if (tracked.State != OrderLifecycleState.PendingReplace ||
                    tracked.PendingReplacement is not { } replacement)
                {
                    return;
                }
                replacementTerms = replacement;
                tracked.CurrentTerms = replacement;
                tracked.PendingReplacement = null;
                tracked.State = tracked.FilledQuantity.Coefficient == 0
                    ? OrderLifecycleState.Working
                    : OrderLifecycleState.PartiallyFilled;
            }
            PublishOrderEvent(
                tracked,
                VenueEventKind.Replaced,
                update.UpdatedAtUtc,
                replacementTerms: replacementTerms);
            return;
        }

        var kind = status switch
        {
            "canceled" => VenueEventKind.Cancelled,
            "expired" => VenueEventKind.Expired,
            "rejected" => VenueEventKind.Rejected,
            _ => (VenueEventKind?)null,
        };
        if (kind.HasValue)
            PublishTerminal(clientOrderId, kind.Value, update.FailureReason, update.UpdatedAtUtc);
    }

    private void PublishFillDelta(TrackedOrder tracked, AlpacaOrderSnapshot update)
    {
        if (!string.Equals(NativeCapabilities.SelectedAssetClass, "us_equity", StringComparison.Ordinal))
        {
            PublishTerminal(
                tracked.Instruction.Identity.ClientOrderId,
                VenueEventKind.OutcomeUnknown,
                "Alpaca crypto order activity lacks exact fee/liquidity evidence and requires reconciliation.",
                update.UpdatedAtUtc);
            return;
        }
        FillExecution? fill = null;
        BrokerPositionEvent? positionEvent = null;
        lock (_gate)
        {
            if (!TrySubtract(update.FilledQuantity, tracked.FilledQuantity, out var delta) || delta.Coefficient <= 0)
                return;
            if (update.FilledAveragePrice is not { } average ||
                !TryIncrementalFillPrice(
                    tracked.FilledQuantity,
                    tracked.FilledAveragePrice,
                    update.FilledQuantity,
                    average,
                    delta,
                    out var price) ||
                !delta.TryGetWholeUnits(out _))
            {
                tracked.State = OrderLifecycleState.Unknown;
                PublishOrderEvent(tracked, VenueEventKind.OutcomeUnknown, update.UpdatedAtUtc, reason: "Alpaca cumulative fill could not be represented exactly.");
                return;
            }

            tracked.FilledQuantity = update.FilledQuantity;
            tracked.FilledAveragePrice = average;
            tracked.Acknowledged = true;
            tracked.State = statusIsFilled(update.Status)
                ? OrderLifecycleState.Filled
                : OrderLifecycleState.PartiallyFilled;
            // Alpaca US-equity PAPER trading is commission-free in this slice, and its polled
            // order snapshot has no maker/taker field. Crypto therefore remains uncertified above;
            // only certified US-equity sessions may reach this zero-fee/Taker canonical mapping.
            fill = new FillExecution(delta, price, ScaledMoney.Zero, LiquidityFlag.Taker);
            if (!TryApplyPosition(_inferredPosition, tracked.CurrentTerms.Side, delta, out var exactPosition))
            {
                tracked.State = OrderLifecycleState.Unknown;
                PublishOrderEvent(
                    tracked,
                    VenueEventKind.OutcomeUnknown,
                    update.UpdatedAtUtc,
                    reason: "The exact Alpaca position update exceeded the supported ScaledQuantity range.");
                return;
            }
            _inferredPosition = exactPosition;
            positionEvent = new BrokerPositionEvent(
                EventId(tracked, "position", update.UpdatedAtUtc, update.FilledQuantity),
                Account,
                tracked.Instruction.Identity.ClientOrderId,
                Utc(update.UpdatedAtUtc),
                tracked.CausationId,
                Instrument,
                _inferredPosition);
            var observedAtUtc = Utc(update.UpdatedAtUtc);
            var positions = _snapshot.Positions
                .Where(item => item.Instrument != Instrument)
                .Append(new BrokerPositionSnapshot(
                    Instrument,
                    _inferredPosition,
                    observedAtUtc))
                .ToArray();
            _snapshot = new BrokerReconciliationSnapshot(
                Account,
                observedAtUtc,
                _snapshot.OpenOrders,
                _snapshot.CompletedOrders,
                Array.AsReadOnly(positions),
                _snapshot.Cash);
        }

        var venueEvent = VenueEvent(
            tracked,
            VenueEventKind.Fill,
            update.UpdatedAtUtc,
            fill: fill);
        var execution = new BrokerExecutionEvent(
            EventId(tracked, "execution", update.UpdatedAtUtc, update.FilledQuantity),
            Account,
            tracked.Instruction.Identity.ClientOrderId,
            Utc(update.UpdatedAtUtc),
            venueEvent);
        Schedule(() => EventReceived?.Invoke(execution));
        Schedule(() => EventReceived?.Invoke(positionEvent!));
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
                VenueEventKind.Replaced => tracked.FilledQuantity.Coefficient == 0 ? OrderLifecycleState.Working : OrderLifecycleState.PartiallyFilled,
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
        var venueEvent = VenueEvent(tracked, kind, occurredAtUtc, replacementTerms: replacementTerms, reason: reason);
        var orderEvent = new BrokerOrderEvent(
            EventId(tracked, kind.ToString(), occurredAtUtc, tracked.FilledQuantity),
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
            Utc(occurredAtUtc),
            tracked.CausationId,
            new DeduplicationKey(eventId),
            reason);
    }

    private VenueOrderSnapshot[] BuildOrderSnapshots(IReadOnlyList<AlpacaOrderSnapshot> orders)
    {
        var result = new List<VenueOrderSnapshot>();
        foreach (var order in orders)
        {
            if (!string.Equals(order.Symbol, Symbol, StringComparison.Ordinal))
                continue;
            if (!TryBuildVenueSnapshot(order, out var snapshot))
                throw new InvalidDataException("An Alpaca reconciliation order could not be represented exactly.");
            result.Add(snapshot!);
        }
        return result.ToArray();
    }

    private void MergeSnapshotOrder(VenueOrderSnapshot order)
    {
        lock (_gate)
        {
            var open = _snapshot.OpenOrders
                .Where(item => item.Instruction.Identity.ClientOrderId != order.Instruction.Identity.ClientOrderId)
                .ToList();
            var completed = _snapshot.CompletedOrders
                .Where(item => item.Instruction.Identity.ClientOrderId != order.Instruction.Identity.ClientOrderId)
                .ToList();
            if (OrderLifecycle.IsTerminal(order.State))
                completed.Add(order);
            else
                open.Add(order);
            _snapshot = new BrokerReconciliationSnapshot(
                Account,
                UtcNow(),
                new ReadOnlyCollection<VenueOrderSnapshot>(open),
                new ReadOnlyCollection<VenueOrderSnapshot>(completed),
                _snapshot.Positions,
                _snapshot.Cash);
        }
    }

    private static AlpacaOrderSnapshot[] SelectAuthoritativeOrders(IEnumerable<AlpacaOrderSnapshot> orders) =>
        orders
            .GroupBy(static item => item.ClientOrderId, StringComparer.Ordinal)
            .Select(static group =>
            {
                var nonReplaced = group
                    .Where(static item => !string.Equals(item.Status, "replaced", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                IEnumerable<AlpacaOrderSnapshot> candidates = nonReplaced.Length == 0 ? group : nonReplaced;
                return candidates
                    .OrderByDescending(static item => item.UpdatedAtUtc)
                    .ThenByDescending(item => AuthorityRank(item.Status))
                    .ThenByDescending(static item => item.OrderId, StringComparer.Ordinal)
                    .First();
            })
            .OrderBy(static item => item.ClientOrderId, StringComparer.Ordinal)
            .ToArray();

    private static int AuthorityRank(string status) => status.ToLowerInvariant() switch
    {
        "partially_filled" => 8,
        "pending_cancel" or "pending_replace" => 7,
        "new" or "accepted" or "pending_new" or "accepted_for_bidding" => 6,
        "filled" => 5,
        "canceled" => 4,
        "expired" => 3,
        "rejected" => 2,
        "replaced" => 0,
        _ => 1,
    };

    private static bool IsOpenStatus(string status) => status.ToLowerInvariant() is
        "new" or
        "accepted" or
        "pending_new" or
        "accepted_for_bidding" or
        "partially_filled" or
        "pending_cancel" or
        "pending_replace";

    private bool TryBuildVenueSnapshot(AlpacaOrderSnapshot order, out VenueOrderSnapshot? snapshot)
    {
        snapshot = null;
        var clientId = new ClientOrderId(order.ClientOrderId);
        if (string.Equals(order.Status, "replaced", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryMapTerms(order, out var terms) || !clientId.IsValid)
            return false;
        var state = MapState(order.Status, order.FilledQuantity, order.Quantity);
        lock (_gate)
        {
            if (_orders.TryGetValue(clientId, out var tracked))
            {
                RememberBrokerId(tracked, order.OrderId);
                tracked.CurrentTerms = terms;
                tracked.State = state;
                tracked.FilledQuantity = order.FilledQuantity;
                tracked.FilledAveragePrice = order.FilledAveragePrice;
                tracked.PendingReplacement = null;
                tracked.Acknowledged = state is not OrderLifecycleState.Acknowledging and not OrderLifecycleState.Unknown;
                snapshot = new VenueOrderSnapshot(
                    tracked.Instruction,
                    terms,
                    state,
                    tracked.BrokerOrderId,
                    null,
                    order.FilledQuantity);
                return true;
            }
        }

        var sideSign = terms.Side == OrderSide.Buy ? 1L : -1L;
        if (!terms.Quantity.TryGetWholeUnits(out var quantity))
            return false;
        var identity = new OrderIdentity(
            new IntentId($"alpaca-reconcile-{order.ClientOrderId}"),
            null,
            new LegId(AdapterId),
            clientId,
            new BrokerOrderId(order.OrderId),
            null,
            new CorrelationId($"alpaca-reconcile-{order.OrderId}"),
            new CausationId($"alpaca-reconcile-{order.OrderId}"),
            new ExecutionLeaseId("alpaca-reconcile"),
            new FencingToken(1));
        var intent = new TradeIntent(
            Instrument,
            TradeIntentQuantityMode.Delta,
            ScaledQuantity.FromWhole(checked(sideSign * quantity)),
            null,
            null,
            ScaledMoney.Zero,
            "alpaca-reconciliation",
            0,
            $"alpaca-{EnvironmentLabel.ToLowerInvariant()}-v1");
        var instruction = new CanonicalOrderInstruction(identity, intent, terms);
        if (instruction.Validate() != OrderDomainFault.None)
            return false;
        snapshot = new VenueOrderSnapshot(
            instruction,
            terms,
            state,
            new BrokerOrderId(order.OrderId),
            null,
            order.FilledQuantity);
        lock (_gate)
        {
            if (!_orders.ContainsKey(clientId) && MakeOrderCapacity())
            {
                var tracked = new TrackedOrder(
                    instruction,
                    terms,
                    state,
                    null,
                    order.FilledQuantity,
                    order.FilledAveragePrice,
                    identity.CausationId)
                {
                    Acknowledged = state is not OrderLifecycleState.Acknowledging and not OrderLifecycleState.Unknown,
                };
                _orders.Add(clientId, tracked);
                _insertionOrder.Enqueue(clientId);
                RememberBrokerId(tracked, order.OrderId);
            }
        }
        return true;
    }

    private bool TryMapTerms(AlpacaOrderSnapshot order, out CanonicalOrderTerms terms)
    {
        terms = default;
        var side = order.Side switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => (OrderSide?)null,
        };
        var type = order.OrderType switch
        {
            "market" => CanonicalOrderType.Market,
            "limit" => CanonicalOrderType.Limit,
            "stop" => CanonicalOrderType.Stop,
            "stop_limit" => CanonicalOrderType.StopLimit,
            _ => (CanonicalOrderType?)null,
        };
        var timeInForce = order.TimeInForce switch
        {
            "day" => CanonicalTimeInForce.Day,
            "gtc" => CanonicalTimeInForce.GoodTillCancelled,
            "ioc" => CanonicalTimeInForce.ImmediateOrCancel,
            "fok" => CanonicalTimeInForce.FillOrKill,
            _ => (CanonicalTimeInForce?)null,
        };
        if (!side.HasValue || !type.HasValue || !timeInForce.HasValue)
            return false;
        terms = new CanonicalOrderTerms(
            side.Value,
            type.Value,
            timeInForce.Value,
            order.Quantity,
            order.LimitPrice,
            order.StopPrice);
        return terms.Validate() == OrderDomainFault.None;
    }

    private bool TryMapSubmit(CanonicalOrderInstruction instruction, out AlpacaSubmitRequest? request, out string? reason)
    {
        request = null;
        reason = null;
        if (!TryNativeType(instruction.Terms.OrderType, out var type) ||
            !TryNativeTimeInForce(instruction.Terms.TimeInForce, out var timeInForce))
        {
            reason = "The Alpaca canonical type or time in force has no exact mapping.";
            return false;
        }
        request = new AlpacaSubmitRequest(
            Symbol,
            instruction.Identity.ClientOrderId.Value,
            instruction.Terms.Side == OrderSide.Buy ? "buy" : "sell",
            type!,
            timeInForce!,
            instruction.Terms.Quantity,
            instruction.Terms.LimitPrice,
            instruction.Terms.StopPrice);
        return true;
    }

    private bool TryMapReplace(CanonicalOrderTerms terms, out AlpacaReplaceRequest? request, out string? reason)
    {
        request = null;
        reason = null;
        if (!TryNativeTimeInForce(terms.TimeInForce, out var timeInForce))
        {
            reason = "The Alpaca replacement time in force has no exact mapping.";
            return false;
        }
        request = new AlpacaReplaceRequest(terms.Quantity, timeInForce!, terms.LimitPrice, terms.StopPrice);
        return true;
    }

    private static bool TryNativeType(CanonicalOrderType value, out string? result)
    {
        result = value switch
        {
            CanonicalOrderType.Market => "market",
            CanonicalOrderType.Limit => "limit",
            CanonicalOrderType.Stop => "stop",
            CanonicalOrderType.StopLimit => "stop_limit",
            _ => null,
        };
        return result is not null;
    }

    private static bool TryNativeTimeInForce(CanonicalTimeInForce value, out string? result)
    {
        result = value switch
        {
            CanonicalTimeInForce.Day => "day",
            CanonicalTimeInForce.GoodTillCancelled => "gtc",
            CanonicalTimeInForce.ImmediateOrCancel => "ioc",
            CanonicalTimeInForce.FillOrKill => "fok",
            _ => null,
        };
        return result is not null;
    }

    private static AlpacaNativeCapabilities DiscoverNativeCapabilities(AlpacaAssetSnapshot asset)
    {
        var minimum = asset.MinimumOrderSize ?? (asset.Fractionable ? new ScaledQuantity(1, 9) : ScaledQuantity.FromWhole(1));
        var increment = asset.MinimumTradeIncrement ?? (asset.Fractionable ? new ScaledQuantity(1, 9) : ScaledQuantity.FromWhole(1));
        var priceIncrement = asset.PriceIncrement ?? new ScaledPrice(1, 2);
        var isCrypto = string.Equals(asset.AssetClass, "crypto", StringComparison.Ordinal);
        return new AlpacaNativeCapabilities(
            isCrypto
                ? Array.AsReadOnly(["market", "limit", "stop_limit"])
                : Array.AsReadOnly(["market", "limit", "stop", "stop_limit", "trailing_stop"]),
            isCrypto
                ? Array.AsReadOnly(["gtc", "ioc"])
                : Array.AsReadOnly(["day", "gtc", "opg", "cls", "ioc", "fok"]),
            Array.AsReadOnly(["us_equity", "crypto"]),
            asset.AssetClass,
            asset.Fractionable,
            asset.Fractionable,
            minimum,
            increment,
            priceIncrement);
    }

    private BrokerExecutionCapabilities DiscoverCanonicalCapabilities(AlpacaAssetSnapshot asset)
    {
        var tick = asset.PriceIncrement ?? new ScaledPrice(1, 2);
        var isCrypto = string.Equals(asset.AssetClass, "crypto", StringComparison.Ordinal);
        var canonical = isCrypto
            ? new VenueCapabilities(
                SupportedOrderTypes.Market | SupportedOrderTypes.Limit | SupportedOrderTypes.StopLimit,
                SupportedTimeInForce.GoodTillCancelled | SupportedTimeInForce.ImmediateOrCancel)
            : VenueCapabilities.All;
        return new BrokerExecutionCapabilities(
            Version: $"alpaca-{EnvironmentLabel.ToLowerInvariant()}-v2-{asset.AssetClass}-{tick.Coefficient}-{tick.Scale}",
            CanonicalCapabilities: canonical,
            QuantityPrecision: 0,
            MinimumQuantity: ScaledQuantity.FromWhole(1),
            MaximumQuantity: ScaledQuantity.FromWhole(1_000_000_000),
            LotSize: ScaledQuantity.FromWhole(1),
            SupportsFractionalQuantity: false,
            PricePrecision: tick.Scale,
            TickSize: tick,
            MinimumPrice: tick,
            MaximumPrice: null,
            ReplaceSemantics: BrokerReplaceSemantics.InPlace,
            SupportsNativeBracket: false,
            SupportsNativeOco: false,
            TradingHours: BrokerTradingHours.AlwaysOpen,
            RateLimit: new BrokerRateLimit(_options.MaximumCommandsPerMinute, TimeSpan.FromMinutes(1)));
    }

    private static AlpacaNativeCapabilities UnavailableNativeCapabilities() => new(
        Array.AsReadOnly<string>([]),
        Array.AsReadOnly<string>([]),
        Array.AsReadOnly<string>([]),
        string.Empty,
        false,
        false,
        null,
        null,
        null);

    private BrokerExecutionCapabilities UnavailableCapabilities() => new(
        Version: $"alpaca-{EnvironmentLabel.ToLowerInvariant()}-unavailable-v1",
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
        ReplaceSemantics: BrokerReplaceSemantics.InPlace,
        SupportsNativeBracket: false,
        SupportsNativeOco: false,
        TradingHours: BrokerTradingHours.AlwaysOpen,
        RateLimit: new BrokerRateLimit(_options.MaximumCommandsPerMinute, TimeSpan.FromMinutes(1)));

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
                return true;
        }
        clientOrderId = default;
        tracked = null;
        return false;
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

    private bool TryValidateLiveAuthorization(out string? reason)
    {
        reason = null;
        if (Mode == ExecutionMode.Paper)
            return true;
        try
        {
            var authorizedEndpoint = AlpacaExecutionEndpointGate.Resolve(_options, _liveConfirmationStore);
            if (authorizedEndpoint != _endpoint || !authorizedEndpoint.IsLive)
                throw new InvalidOperationException("The Alpaca LIVE endpoint no longer matches its gated endpoint token.");
            return true;
        }
        catch (Exception exception)
        {
            reason = $"Alpaca LIVE authorization is absent or was revoked: {SafeReason(exception)}";
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
        CancellationTokenSource? connectionLifetime;
        lock (_gate)
            connectionLifetime = _connectionLifetime;
        try
        {
            connectionLifetime?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent disconnect already closed this lifetime; execution stays revoked.
        }
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
    }

    /// <summary>Called only while holding <see cref="_gate"/>.</summary>
    private void ClearTrackedState()
    {
        _orders.Clear();
        _brokerToClient.Clear();
        _insertionOrder.Clear();
        _inferredPosition = ScaledQuantity.Zero;
        _snapshot = EmptySnapshot(UtcNow());
    }

    private void RememberBrokerId(TrackedOrder tracked, string orderId)
    {
        var brokerOrderId = new BrokerOrderId(orderId);
        if (!brokerOrderId.IsValid)
            throw new InvalidDataException("Alpaca returned an invalid broker order ID.");
        if (_brokerToClient.TryGetValue(brokerOrderId, out var existingClient) &&
            existingClient != tracked.Instruction.Identity.ClientOrderId)
        {
            throw new InvalidDataException("Alpaca reused one broker order ID for different client orders.");
        }
        if (tracked.BrokerOrderId is { } prior)
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
            if (tracked.BrokerOrderId is { } brokerOrderId)
                _brokerToClient.Remove(brokerOrderId);
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

    private void OnUpdateSourceFaulted(Exception exception) =>
        SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, Session.IsExecutionAuthenticated, false);

    private void OnSchedulerFaulted(Exception exception) =>
        SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, Session.IsExecutionAuthenticated, false);

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
        _updateSource.OrderUpdated -= OnOrderUpdated;
        _updateSource.Faulted -= OnUpdateSourceFaulted;
        if (_ownedScheduler is not null)
            _ownedScheduler.CallbackFaulted -= OnSchedulerFaulted;
    }

    private async Task SafeDisconnectTransportAsync()
    {
        try
        {
            await _updateSource.StopAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        catch
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

    private BrokerDispatchReceipt CreateReceipt(
        BrokerAdapterCommandKind kind,
        ClientOrderId clientOrderId,
        CausationId causationId)
    {
        var material = $"{AdapterId}|{Account.AccountId.Value}|{kind}|{clientOrderId.Value}|{causationId.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerDispatchReceipt(
            new DispatchReceiptId($"alpaca-{Convert.ToHexString(hash).ToLowerInvariant()}"),
            Account,
            kind,
            clientOrderId,
            causationId,
            UtcNow());
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
        Array.AsReadOnly(snapshot.OpenOrders.ToArray()),
        Array.AsReadOnly(snapshot.CompletedOrders.ToArray()),
        Array.AsReadOnly(snapshot.Positions.ToArray()),
        Array.AsReadOnly(snapshot.Cash.ToArray()));

    private static OrderLifecycleState MapState(string status, ScaledQuantity filled, ScaledQuantity requested) =>
        status.ToLowerInvariant() switch
        {
            "new" or "accepted" or "pending_new" or "accepted_for_bidding" => OrderLifecycleState.Working,
            "partially_filled" => OrderLifecycleState.PartiallyFilled,
            "filled" => OrderLifecycleState.Filled,
            "canceled" => OrderLifecycleState.Cancelled,
            "expired" => OrderLifecycleState.Expired,
            "rejected" => OrderLifecycleState.Rejected,
            "pending_cancel" => OrderLifecycleState.PendingCancel,
            "pending_replace" => OrderLifecycleState.PendingReplace,
            "replaced" => OrderLifecycleState.Unknown,
            _ => OrderLifecycleState.Unknown,
        };

    private static bool TrySubtract(ScaledQuantity left, ScaledQuantity right, out ScaledQuantity result)
    {
        result = default;
        if (!ScaledValueMath.TryAlign(
                left.Coefficient,
                left.Scale,
                right.Coefficient,
                right.Scale,
                out var alignedLeft,
                out var alignedRight,
                out var scale) ||
            !ScaledValueMath.TryNarrow(alignedLeft - alignedRight, scale, out var coefficient, out var narrowedScale))
            return false;
        result = new ScaledQuantity(coefficient, narrowedScale);
        return true;
    }

    private static bool TryApplyPosition(
        ScaledQuantity current,
        OrderSide side,
        ScaledQuantity fill,
        out ScaledQuantity result)
    {
        result = default;
        var signedFill = side == OrderSide.Buy ? (Int128)fill.Coefficient : -(Int128)fill.Coefficient;
        if (!ScaledValueMath.TryAdd(
                current.Coefficient,
                current.Scale,
                signedFill,
                fill.Scale,
                out var coefficient,
                out var scale) ||
            !ScaledValueMath.TryNarrow(coefficient, scale, out var narrowed, out var narrowedScale))
        {
            return false;
        }
        result = new ScaledQuantity(narrowed, narrowedScale);
        return true;
    }

    private static bool TryIncrementalFillPrice(
        ScaledQuantity priorQuantity,
        ScaledPrice? priorAverage,
        ScaledQuantity cumulativeQuantity,
        ScaledPrice cumulativeAverage,
        ScaledQuantity deltaQuantity,
        out ScaledPrice price)
    {
        price = default;
        if (priorQuantity.Coefficient == 0)
        {
            price = cumulativeAverage;
            return price.Coefficient > 0;
        }
        if (priorAverage is not { } oldAverage ||
            !priorQuantity.TryGetWholeUnits(out var priorUnits) ||
            !cumulativeQuantity.TryGetWholeUnits(out var cumulativeUnits) ||
            !deltaQuantity.TryGetWholeUnits(out var deltaUnits) ||
            deltaUnits <= 0 ||
            !ScaledValueMath.TryAlign(
                (Int128)oldAverage.Coefficient * priorUnits,
                oldAverage.Scale,
                (Int128)cumulativeAverage.Coefficient * cumulativeUnits,
                cumulativeAverage.Scale,
                out var priorNotional,
                out var cumulativeNotional,
                out var scale))
            return false;
        var deltaNotional = cumulativeNotional - priorNotional;
        for (var extraScale = 0; scale + extraScale <= ScaledValueMath.MaximumScale; extraScale++)
        {
            if (!ScaledValueMath.TryMultiplyPower10(deltaNotional, extraScale, out var scaled) || scaled % deltaUnits != 0)
                continue;
            if (!ScaledValueMath.TryNarrow(scaled / deltaUnits, scale + extraScale, out var coefficient, out var narrowedScale))
                return false;
            price = new ScaledPrice(coefficient, narrowedScale);
            return coefficient > 0;
        }
        return false;
    }

    private BrokerAdapterEventId EventId(TrackedOrder tracked, string kind, DateTime occurredAtUtc, ScaledQuantity quantity)
    {
        var material = $"{AdapterId}|{tracked.Instruction.Identity.ClientOrderId.Value}|{tracked.BrokerOrderId?.Value}|{kind}|{Utc(occurredAtUtc).Ticks}|{quantity.Coefficient}:{quantity.Scale}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerAdapterEventId($"alpaca-event-{Convert.ToHexString(hash).ToLowerInvariant()}");
    }

    private DateTime UtcNow() => Utc(_clock.UtcNow);

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool statusIsFilled(string status) =>
        string.Equals(status, "filled", StringComparison.OrdinalIgnoreCase);

    private static string SafeReason(Exception exception)
    {
        var message = exception.Message;
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : message.Length <= 256 ? message : message[..256];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TrackedOrder(
        CanonicalOrderInstruction instruction,
        CanonicalOrderTerms currentTerms,
        OrderLifecycleState state,
        BrokerOrderId? brokerOrderId,
        ScaledQuantity filledQuantity,
        ScaledPrice? filledAveragePrice,
        CausationId causationId)
    {
        internal CanonicalOrderInstruction Instruction { get; } = instruction;
        internal CanonicalOrderTerms CurrentTerms { get; set; } = currentTerms;
        internal OrderLifecycleState State { get; set; } = state;
        internal BrokerOrderId? BrokerOrderId { get; set; } = brokerOrderId;
        internal ScaledQuantity FilledQuantity { get; set; } = filledQuantity;
        internal ScaledPrice? FilledAveragePrice { get; set; } = filledAveragePrice;
        internal CausationId CausationId { get; set; } = causationId;
        internal CanonicalOrderTerms? PendingReplacement { get; set; }
        internal bool Acknowledged { get; set; }
    }
}
