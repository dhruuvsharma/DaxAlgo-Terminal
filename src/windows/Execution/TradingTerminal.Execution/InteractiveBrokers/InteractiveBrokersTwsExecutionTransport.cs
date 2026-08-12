#if HAS_IBAPI
using System.Globalization;
using IBApi;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>
/// Real TWS/IB Gateway socket transport. It owns one <see cref="EClientSocket"/>, one native
/// <see cref="EReader"/>, and one long-lived message-processing thread. EWrapper callbacks are
/// forwarded synchronously to the adapter bridge; no callback creates a task.
/// </summary>
public sealed class InteractiveBrokersTwsExecutionTransport : DefaultEWrapper, IInteractiveBrokersExecutionTransport
{
    private const int FirstRequestId = 1_000_000;
    private static readonly IReadOnlyList<InteractiveBrokersNativeTimeInForce> SupportedTimeInForce =
        Array.AsReadOnly(new[]
        {
            InteractiveBrokersNativeTimeInForce.Day,
            InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
            InteractiveBrokersNativeTimeInForce.ImmediateOrCancel,
            InteractiveBrokersNativeTimeInForce.FillOrKill,
            InteractiveBrokersNativeTimeInForce.MarketOnOpen,
        });

    private readonly object _stateGate = new();
    private readonly object _sendGate = new();
    private readonly EReaderMonitorSignal _signal = new();
    private readonly EClientSocket _client;
    private readonly TimeSpan _timeout;
    private readonly int _maximumTrackedOrders;
    private readonly Dictionary<int, PendingContractDetails> _contractRequests = [];
    private readonly Dictionary<int, InteractiveBrokersOrderRequest> _requests = [];
    private readonly Dictionary<int, InteractiveBrokersOrderSnapshot> _orders = [];
    private readonly Dictionary<int, string> _clientOrderIds = [];
    private readonly Dictionary<string, int> _nativeOrderIds = new(StringComparer.Ordinal);
    private readonly HashSet<int> _trackedOrderIds = [];
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private EReader? _reader;
    private Thread? _messageThread;
    private TaskCompletionSource<int>? _nextValidOrderIdSource;
    private TaskCompletionSource<IReadOnlyList<string>>? _managedAccountsSource;
    private ReconciliationRequest? _reconciliation;
    private string? _accountId;
    private int _nextOrderId;
    private int _nextRequestId = FirstRequestId;
    private bool _hasNextOrderId;
    private bool _stopReader;
    private bool _disposed;

    /// <summary>Creates a socket transport only for an endpoint produced by the central gate.</summary>
    public InteractiveBrokersTwsExecutionTransport(
        InteractiveBrokersExecutionEndpoint endpoint,
        TimeSpan timeout,
        int maximumTrackedOrders = InteractiveBrokersExecutionTransportFactory.DefaultMaximumTrackedOrders)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAuthorized)
            throw new InvalidOperationException("The IB transport requires an exact endpoint produced by the authorization gate.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumTrackedOrders is < 32 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedOrders));
        Endpoint = endpoint;
        _timeout = timeout;
        _maximumTrackedOrders = maximumTrackedOrders;
        _client = new EClientSocket(this, _signal);
    }

    /// <inheritdoc />
    public InteractiveBrokersExecutionEndpoint Endpoint { get; }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
                return !_disposed && _client.IsConnected() && _messageThread is { IsAlive: true };
        }
    }

    /// <inheritdoc />
    public event Action<InteractiveBrokersOrderSnapshot>? OrderUpdated;

    /// <inheritdoc />
    public event Action<InteractiveBrokersExecutionSnapshot>? ExecutionReceived;

    /// <inheritdoc />
    public event Action<InteractiveBrokersCommissionSnapshot>? CommissionReceived;

    /// <inheritdoc />
    public event Action<InteractiveBrokersPositionSnapshot>? PositionUpdated;

    /// <inheritdoc />
    public event Action<InteractiveBrokersOrderError>? OrderError;

    /// <inheritdoc />
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public async Task<InteractiveBrokersSessionSnapshot> ConnectAsync(
        int clientId,
        string expectedAccountId,
        CancellationToken cancellationToken = default)
    {
        if (clientId < 0)
            throw new ArgumentOutOfRangeException(nameof(clientId));
        expectedAccountId ??= string.Empty;
        if (!string.Equals(expectedAccountId, expectedAccountId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The expected IB account ID must be trimmed.", nameof(expectedAccountId));
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<int> orderIdSource;
        TaskCompletionSource<IReadOnlyList<string>> accountsSource;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client.IsConnected() || _messageThread is { IsAlive: true })
                throw new InvalidOperationException("The Interactive Brokers transport is already connected.");
            _stopReader = false;
            _accountId = null;
            _hasNextOrderId = false;
            orderIdSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            accountsSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextValidOrderIdSource = orderIdSource;
            _managedAccountsSource = accountsSource;
        }

        try
        {
            lock (_sendGate)
                _client.eConnect(Endpoint.Host, Endpoint.Port, clientId);
            if (!_client.IsConnected())
                throw new InvalidOperationException("TWS or IB Gateway did not open the requested socket session.");

            var reader = new EReader(_client, _signal);
            reader.Start();
            var messageThread = new Thread(() => ProcessMessages(reader))
            {
                IsBackground = true,
                Name = "DaxAlgo.IB.Execution.EReader",
            };
            lock (_stateGate)
            {
                _reader = reader;
                _messageThread = messageThread;
            }
            messageThread.Start();

            lock (_sendGate)
                _client.reqIds(1);

            var nextOrderId = await orderIdSource.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            var accounts = await accountsSource.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            var authenticatedAccount = ResolveAuthenticatedAccount(accounts, expectedAccountId);
            lock (_stateGate)
                _accountId = authenticatedAccount;
            return new InteractiveBrokersSessionSnapshot(
                authenticatedAccount,
                nextOrderId,
                DateTime.UtcNow,
                Endpoint.IsPaper);
        }
        catch
        {
            DisconnectCore();
            throw;
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_nextValidOrderIdSource, orderIdSource))
                    _nextValidOrderIdSource = null;
                if (ReferenceEquals(_managedAccountsSource, accountsSource))
                    _managedAccountsSource = null;
            }
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCore();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<InteractiveBrokersNativeCapabilities> DiscoverCapabilitiesAsync(
        InteractiveBrokersContract contract,
        CancellationToken cancellationToken = default)
    {
        ValidateContract(contract);
        EnsureConnected();
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingContractDetails(contract);
        lock (_stateGate)
        {
            if (!_contractRequests.TryAdd(requestId, pending))
                throw new InvalidOperationException("An IB contract-details request ID collided.");
        }

        try
        {
            lock (_sendGate)
                _client.reqContractDetails(requestId, ToNativeContract(contract));
            return await pending.Completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
                _contractRequests.Remove(requestId);
        }
    }

    /// <inheritdoc />
    public int ReserveOrderId()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_client.IsConnected() || !_hasNextOrderId)
                throw new InvalidOperationException("An authenticated IB session has not supplied a next valid order ID.");
            if (_nextOrderId == int.MaxValue)
                throw new InvalidOperationException("The IB native order-ID range is exhausted.");
            return _nextOrderId++;
        }
    }

    /// <inheritdoc />
    public Task PlaceOrderAsync(
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOrderRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnectedForAccount(request.AccountId);

        lock (_stateGate)
        {
            if (_requests.TryGetValue(request.OrderId, out var prior))
            {
                if (prior == request)
                    return Task.CompletedTask;
                throw new InvalidOperationException("The native IB order ID is already bound to a different payload; use ModifyOrderAsync.");
            }
            if (_nativeOrderIds.TryGetValue(request.ClientOrderId, out var existingId) && existingId != request.OrderId)
                throw new InvalidOperationException("The client order ID is already bound to a different native IB order ID.");
            RememberRequest(request);
        }

        try
        {
            lock (_sendGate)
                _client.placeOrder(request.OrderId, ToNativeContract(request.Contract), ToNativeOrder(request));
            return Task.CompletedTask;
        }
        catch
        {
            lock (_stateGate)
                ForgetRequest(request.OrderId, request.ClientOrderId);
            throw;
        }
    }

    /// <inheritdoc />
    public Task CancelOrderAsync(
        int orderId,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        if (orderId < 0 || string.IsNullOrWhiteSpace(clientOrderId) || clientOrderId.Length > 256)
            throw new ArgumentException("A bounded native/client IB order identity is required.");
        lock (_stateGate)
        {
            if (!_clientOrderIds.TryGetValue(orderId, out var mapped) ||
                !string.Equals(mapped, clientOrderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The native/client IB order identity is not tracked by this transport.");
            }
        }
        lock (_sendGate)
            _client.cancelOrder(orderId, new OrderCancel());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ModifyOrderAsync(
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOrderRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnectedForAccount(request.AccountId);
        lock (_stateGate)
        {
            if (!_clientOrderIds.TryGetValue(request.OrderId, out var mapped) ||
                !string.Equals(mapped, request.ClientOrderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The native/client IB order identity is not tracked by this transport.");
            }
            _requests[request.OrderId] = request;
        }
        lock (_sendGate)
            _client.placeOrder(request.OrderId, ToNativeContract(request.Contract), ToNativeOrder(request));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<InteractiveBrokersReconciliationSnapshot> GetReconciliationSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 128 ||
            !string.Equals(accountId, accountId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A bounded exact IB account ID is required.", nameof(accountId));
        }
        EnsureConnectedForAccount(accountId);
        await _reconciliationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ReconciliationRequest? request = null;
        try
        {
            var accountSummaryRequestId = Interlocked.Increment(ref _nextRequestId);
            request = new ReconciliationRequest(accountId, accountSummaryRequestId, _maximumTrackedOrders);
            lock (_stateGate)
            {
                if (_reconciliation is not null)
                    throw new InvalidOperationException("An IB reconciliation snapshot is already in progress.");
                _reconciliation = request;
            }

            lock (_sendGate)
            {
                _client.reqOpenOrders();
                _client.reqCompletedOrders(apiOnly: true);
                _client.reqPositions();
                _client.reqAccountSummary(
                    accountSummaryRequestId,
                    "All",
                    "TotalCashValue,AvailableFunds");
            }
            return await request.Completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (request is not null)
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_reconciliation, request))
                        _reconciliation = null;
                }
                if (_client.IsConnected())
                {
                    lock (_sendGate)
                    {
                        _client.cancelPositions();
                        _client.cancelAccountSummary(request.AccountSummaryRequestId);
                    }
                }
            }
            _reconciliationGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        DisconnectCore();
        _reconciliationGate.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override void nextValidId(int orderId)
    {
        TaskCompletionSource<int>? source;
        lock (_stateGate)
        {
            if (!_hasNextOrderId || orderId > _nextOrderId)
                _nextOrderId = orderId;
            _hasNextOrderId = true;
            source = _nextValidOrderIdSource;
        }
        source?.TrySetResult(orderId);
    }

    /// <inheritdoc />
    public override void managedAccounts(string accountsList)
    {
        var accounts = (accountsList ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static account => account.Length <= 128)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        TaskCompletionSource<IReadOnlyList<string>>? source;
        lock (_stateGate)
            source = _managedAccountsSource;
        source?.TrySetResult(Array.AsReadOnly(accounts));
    }

    /// <inheritdoc />
    public override void contractDetails(int reqId, ContractDetails contractDetails)
    {
        lock (_stateGate)
        {
            if (_contractRequests.TryGetValue(reqId, out var request))
                request.Details.Add(contractDetails);
        }
    }

    /// <inheritdoc />
    public override void contractDetailsEnd(int reqId)
    {
        PendingContractDetails? pending;
        lock (_stateGate)
            _contractRequests.TryGetValue(reqId, out pending);
        if (pending is null)
            return;
        try
        {
            pending.Completion.TrySetResult(BuildCapabilities(pending.RequestedContract, pending.Details));
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    /// <inheritdoc />
    public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        var snapshot = BuildOrderSnapshot(orderId, contract, order, orderState, completed: false);
        RememberOrderSnapshot(snapshot);
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation &&
                string.Equals(snapshot.AccountId, reconciliation.AccountId, StringComparison.Ordinal))
            {
                reconciliation.RememberOpenOrder(snapshot);
            }
        }
        Raise(OrderUpdated, snapshot);
    }

    /// <inheritdoc />
    public override void openOrderEnd()
    {
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation)
            {
                reconciliation.OpenOrdersComplete = true;
                reconciliation.TryComplete();
            }
        }
    }

    /// <inheritdoc />
    public override void orderStatus(
        int orderId,
        string status,
        decimal filled,
        decimal remaining,
        double avgFillPrice,
        long permId,
        int parentId,
        double lastFillPrice,
        int clientId,
        string whyHeld,
        double mktCapPrice)
    {
        InteractiveBrokersOrderSnapshot? snapshot;
        lock (_stateGate)
        {
            if (!_orders.TryGetValue(orderId, out var prior))
                return;
            snapshot = prior with
            {
                PermanentId = permId,
                Status = ParseOrderStatus(status),
                NativeStatus = status ?? string.Empty,
                FilledQuantity = ToScaledQuantity(filled),
                RemainingQuantity = ToScaledQuantity(remaining),
                WhyHeld = NullIfWhiteSpace(whyHeld),
                UpdatedAtUtc = DateTime.UtcNow,
            };
            _orders[orderId] = snapshot;
        }
        Raise(OrderUpdated, snapshot);
    }

    /// <inheritdoc />
    public override void completedOrder(Contract contract, Order order, OrderState orderState)
    {
        var snapshot = BuildOrderSnapshot(order.OrderId, contract, order, orderState, completed: true);
        RememberOrderSnapshot(snapshot);
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation &&
                string.Equals(snapshot.AccountId, reconciliation.AccountId, StringComparison.Ordinal))
            {
                reconciliation.RememberCompletedOrder(snapshot);
            }
        }
        Raise(OrderUpdated, snapshot);
    }

    /// <inheritdoc />
    public override void completedOrdersEnd()
    {
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation)
            {
                reconciliation.CompletedOrdersComplete = true;
                reconciliation.TryComplete();
            }
        }
    }

    /// <inheritdoc />
    public override void execDetails(int reqId, Contract contract, IBApi.Execution execution)
    {
        string clientOrderId;
        lock (_stateGate)
        {
            clientOrderId = !string.IsNullOrWhiteSpace(execution.OrderRef)
                ? execution.OrderRef
                : _clientOrderIds.GetValueOrDefault(execution.OrderId, string.Empty);
        }
        var snapshot = new InteractiveBrokersExecutionSnapshot(
            execution.ExecId ?? string.Empty,
            execution.OrderId,
            execution.PermId,
            clientOrderId,
            execution.AcctNumber ?? string.Empty,
            FromNativeContract(contract),
            execution.Side ?? string.Empty,
            ToScaledQuantity(execution.Shares),
            ToScaledPrice(execution.Price),
            ToScaledQuantity(execution.CumQty),
            ToScaledPrice(execution.AvgPrice),
            execution.Time ?? string.Empty,
            DateTime.UtcNow);
        Raise(ExecutionReceived, snapshot);
    }

    /// <inheritdoc />
    public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport)
    {
        ScaledMoney? realized = double.IsFinite(commissionAndFeesReport.RealizedPNL) &&
                                commissionAndFeesReport.RealizedPNL != double.MaxValue
            ? ToScaledMoney(commissionAndFeesReport.RealizedPNL)
            : null;
        Raise(
            CommissionReceived,
            new InteractiveBrokersCommissionSnapshot(
                commissionAndFeesReport.ExecId ?? string.Empty,
                ToScaledMoney(commissionAndFeesReport.CommissionAndFees),
                commissionAndFeesReport.Currency ?? string.Empty,
                realized,
                DateTime.UtcNow));
    }

    /// <inheritdoc />
    public override void position(string account, Contract contract, decimal pos, double avgCost)
    {
        var snapshot = new InteractiveBrokersPositionSnapshot(
            account ?? string.Empty,
            FromNativeContract(contract),
            ToScaledQuantity(pos),
            ToScaledPrice(avgCost),
            DateTime.UtcNow);
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation &&
                string.Equals(snapshot.AccountId, reconciliation.AccountId, StringComparison.Ordinal))
            {
                reconciliation.RememberPosition(snapshot);
            }
        }
        Raise(PositionUpdated, snapshot);
    }

    /// <inheritdoc />
    public override void positionEnd()
    {
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation)
            {
                reconciliation.PositionsComplete = true;
                reconciliation.TryComplete();
            }
        }
    }

    /// <inheritdoc />
    public override void accountSummary(
        int reqId,
        string account,
        string tag,
        string value,
        string currency)
    {
        lock (_stateGate)
        {
            if (_reconciliation is not { } reconciliation ||
                reconciliation.AccountSummaryRequestId != reqId ||
                !string.Equals(reconciliation.AccountId, account, StringComparison.Ordinal) ||
                !TryParseScaled(value, out var coefficient, out var scale))
            {
                return;
            }
            reconciliation.RememberCash(
                currency ?? string.Empty,
                tag,
                new ScaledMoney(coefficient, scale));
        }
    }

    /// <inheritdoc />
    public override void accountSummaryEnd(int reqId)
    {
        lock (_stateGate)
        {
            if (_reconciliation is { } reconciliation &&
                reconciliation.AccountSummaryRequestId == reqId)
            {
                reconciliation.CashComplete = true;
                reconciliation.TryComplete();
            }
        }
    }

    /// <inheritdoc />
    public override void error(Exception e) => ReportFault(e);

    /// <inheritdoc />
    public override void error(string str) =>
        ReportFault(new InvalidOperationException(string.IsNullOrWhiteSpace(str) ? "IB reported an unspecified error." : str));

    /// <inheritdoc />
    public override void error(
        int id,
        long errorTime,
        int errorCode,
        string errorMsg,
        string advancedOrderRejectJson)
    {
        string? clientOrderId;
        PendingContractDetails? contractRequest;
        lock (_stateGate)
        {
            clientOrderId = _clientOrderIds.GetValueOrDefault(id);
            _contractRequests.TryGetValue(id, out contractRequest);
        }
        var exception = new InvalidOperationException($"IB error {errorCode}: {errorMsg}");
        contractRequest?.Completion.TrySetException(exception);

        if (id >= 0)
        {
            var orderError = new InteractiveBrokersOrderError(
                id,
                clientOrderId,
                errorCode,
                errorMsg ?? string.Empty,
                NullIfWhiteSpace(advancedOrderRejectJson),
                DateTime.UtcNow);
            Raise(OrderError, orderError);
        }

        if (errorCode is 502 or 504 or 1100 or 1300)
            ReportFault(exception);
    }

    /// <inheritdoc />
    public override void connectionClosed()
    {
        if (!_stopReader)
            ReportFault(new IOException("The TWS or IB Gateway execution connection closed."));
        FailPending(new IOException("The TWS or IB Gateway execution connection closed."));
    }

    private void ProcessMessages(EReader reader)
    {
        try
        {
            while (!_stopReader && _client.IsConnected())
            {
                _signal.waitForSignal();
                if (_stopReader || !_client.IsConnected())
                    break;
                reader.processMsgs();
            }
        }
        catch (Exception exception)
        {
            if (!_stopReader)
                ReportFault(exception);
        }
    }

    private void DisconnectCore()
    {
        Thread? messageThread;
        lock (_stateGate)
        {
            _stopReader = true;
            messageThread = _messageThread;
            _messageThread = null;
            _reader = null;
            _accountId = null;
            _hasNextOrderId = false;
        }
        try
        {
            lock (_sendGate)
            {
                if (_client.IsConnected())
                    _client.eDisconnect();
            }
        }
        finally
        {
            _signal.issueSignal();
            if (messageThread is not null &&
                messageThread != Thread.CurrentThread &&
                !messageThread.Join(_timeout))
            {
                ReportFault(new TimeoutException("The IB message-processing thread did not stop within the bounded timeout."));
            }
            FailPending(new IOException("The Interactive Brokers transport disconnected."));
        }
    }

    private void FailPending(Exception exception)
    {
        TaskCompletionSource<int>? orderSource;
        TaskCompletionSource<IReadOnlyList<string>>? accountSource;
        PendingContractDetails[] contracts;
        ReconciliationRequest? reconciliation;
        lock (_stateGate)
        {
            orderSource = _nextValidOrderIdSource;
            accountSource = _managedAccountsSource;
            contracts = _contractRequests.Values.ToArray();
            reconciliation = _reconciliation;
        }
        orderSource?.TrySetException(exception);
        accountSource?.TrySetException(exception);
        foreach (var contract in contracts)
            contract.Completion.TrySetException(exception);
        reconciliation?.Completion.TrySetException(exception);
    }

    private void EnsureConnected()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_client.IsConnected() || _messageThread is not { IsAlive: true })
                throw new InvalidOperationException("The Interactive Brokers transport is disconnected.");
        }
    }

    private void EnsureConnectedForAccount(string accountId)
    {
        EnsureConnected();
        lock (_stateGate)
        {
            if (!string.Equals(_accountId, accountId, StringComparison.Ordinal))
                throw new InvalidOperationException("The command account does not match the authenticated IB session.");
        }
    }

    private void RememberRequest(InteractiveBrokersOrderRequest request)
    {
        if (_clientOrderIds.TryGetValue(request.OrderId, out var mappedClientOrderId) &&
            !string.Equals(mappedClientOrderId, request.ClientOrderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The native IB order ID is already bound to a different client order identity.");
        }
        if (_nativeOrderIds.TryGetValue(request.ClientOrderId, out var mappedOrderId) &&
            mappedOrderId != request.OrderId)
        {
            throw new InvalidOperationException(
                "The client order ID is already bound to a different native IB order identity.");
        }
        if (!_trackedOrderIds.Contains(request.OrderId))
        {
            if (!TryMakeTrackedOrderCapacity())
            {
                throw new InvalidOperationException(
                    "The bounded IB transport order table is full of active mappings.");
            }
            _trackedOrderIds.Add(request.OrderId);
        }
        _requests[request.OrderId] = request;
        _clientOrderIds[request.OrderId] = request.ClientOrderId;
        _nativeOrderIds[request.ClientOrderId] = request.OrderId;
    }

    private void ForgetRequest(int orderId, string clientOrderId)
    {
        _requests.Remove(orderId);
        _orders.Remove(orderId);
        _clientOrderIds.Remove(orderId);
        _nativeOrderIds.Remove(clientOrderId);
        _trackedOrderIds.Remove(orderId);
    }

    private void RememberOrderSnapshot(InteractiveBrokersOrderSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.ClientOrderId))
            {
                if (_clientOrderIds.TryGetValue(snapshot.OrderId, out var mappedClientOrderId) &&
                    !string.Equals(mappedClientOrderId, snapshot.ClientOrderId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "IB returned one native order ID with conflicting client order identities.");
                }
                if (_nativeOrderIds.TryGetValue(snapshot.ClientOrderId, out var mappedOrderId) &&
                    mappedOrderId != snapshot.OrderId)
                {
                    throw new InvalidDataException(
                        "IB returned one client order ID with conflicting native order identities.");
                }
            }
            if (!_trackedOrderIds.Contains(snapshot.OrderId))
            {
                if (!TryMakeTrackedOrderCapacity())
                {
                    throw new InvalidOperationException(
                        "The bounded IB transport order table is full of active mappings.");
                }
                _trackedOrderIds.Add(snapshot.OrderId);
            }
            if (!string.IsNullOrWhiteSpace(snapshot.ClientOrderId))
            {
                _clientOrderIds[snapshot.OrderId] = snapshot.ClientOrderId;
                _nativeOrderIds[snapshot.ClientOrderId] = snapshot.OrderId;
            }
            _orders[snapshot.OrderId] = snapshot;
        }
    }

    private bool TryMakeTrackedOrderCapacity()
    {
        while (_trackedOrderIds.Count >= _maximumTrackedOrders)
        {
            int? terminalOrderId = null;
            foreach (var orderId in _trackedOrderIds)
            {
                if (_orders.TryGetValue(orderId, out var snapshot) && IsTerminal(snapshot.Status))
                {
                    terminalOrderId = orderId;
                    break;
                }
            }
            if (!terminalOrderId.HasValue)
                return false;
            RemoveTrackedOrder(terminalOrderId.Value);
        }
        return true;
    }

    private void RemoveTrackedOrder(int orderId)
    {
        if (_clientOrderIds.Remove(orderId, out var clientOrderId))
            _nativeOrderIds.Remove(clientOrderId);
        _requests.Remove(orderId);
        _orders.Remove(orderId);
        _trackedOrderIds.Remove(orderId);
    }

    private static bool IsTerminal(InteractiveBrokersNativeOrderStatus status) => status is
        InteractiveBrokersNativeOrderStatus.Cancelled or
        InteractiveBrokersNativeOrderStatus.Filled or
        InteractiveBrokersNativeOrderStatus.Inactive or
        InteractiveBrokersNativeOrderStatus.ApiCancelled or
        InteractiveBrokersNativeOrderStatus.Rejected;

    private InteractiveBrokersOrderSnapshot BuildOrderSnapshot(
        int orderId,
        Contract contract,
        Order order,
        OrderState orderState,
        bool completed)
    {
        InteractiveBrokersOrderSnapshot? prior;
        lock (_stateGate)
            _orders.TryGetValue(orderId, out prior);
        var quantity = ToScaledQuantity(order.TotalQuantity);
        var filled = prior?.FilledQuantity ?? ScaledQuantity.Zero;
        var remaining = TrySubtract(quantity, filled, out var difference) ? difference : quantity;
        var nativeStatus = completed && !string.IsNullOrWhiteSpace(orderState.CompletedStatus)
            ? orderState.CompletedStatus
            : orderState.Status;
        return new InteractiveBrokersOrderSnapshot(
            orderId,
            order.PermId,
            !string.IsNullOrWhiteSpace(order.OrderRef)
                ? order.OrderRef
                : prior?.ClientOrderId ?? string.Empty,
            order.Account ?? string.Empty,
            FromNativeContract(contract),
            order.Action ?? string.Empty,
            ParseOrderType(order.OrderType),
            ParseTimeInForce(order.Tif),
            ParseOrderStatus(nativeStatus),
            nativeStatus ?? string.Empty,
            quantity,
            filled,
            remaining,
            OptionalPrice(order.LmtPrice),
            OrderUsesStopPrice(order.OrderType) ? OptionalPrice(order.AuxPrice) : null,
            OptionalPrice(order.TrailStopPrice),
            OptionalRatio(order.TrailingPercent),
            order.OutsideRth,
            prior?.WhyHeld,
            prior?.RejectionCode,
            NullIfWhiteSpace(orderState.WarningText) ?? prior?.RejectionReason,
            DateTime.UtcNow);
    }

    private static InteractiveBrokersNativeCapabilities BuildCapabilities(
        InteractiveBrokersContract requestedContract,
        IReadOnlyList<ContractDetails> details)
    {
        if (details.Count == 0)
            throw new InvalidDataException("IB returned no contract details for capability discovery.");
        var selected = requestedContract.ContractId > 0
            ? details.FirstOrDefault(item => item.Contract.ConId == requestedContract.ContractId)
            : details.FirstOrDefault();
        selected ??= details[0];

        var orderTypes = (selected.OrderTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseOrderType)
            .Where(static item => item != InteractiveBrokersNativeOrderType.Unknown)
            .Distinct()
            .ToArray();
        if (orderTypes.Length == 0)
            throw new InvalidDataException("IB contract details declared no supported execution order types.");
        if (!double.IsFinite(selected.MinTick) || selected.MinTick <= 0 || selected.MinTick == double.MaxValue)
            throw new InvalidDataException("IB contract details omitted a valid minimum price increment.");

        var minimum = selected.MinSize is > 0 and < decimal.MaxValue
            ? ToScaledQuantity(selected.MinSize)
            : ScaledQuantity.FromWhole(1);
        var increment = selected.SizeIncrement is > 0 and < decimal.MaxValue
            ? ToScaledQuantity(selected.SizeIncrement)
            : ScaledQuantity.FromWhole(1);
        var nativeContract = FromNativeContract(selected.Contract);
        var tradingHoursSchedule = ParseDatedTradingHours(
            selected.TradingHours,
            selected.TimeZoneId,
            "trading-hours");
        var regularHours = ParseDatedTradingHours(
            selected.LiquidHours,
            selected.TimeZoneId,
            "liquid-hours");
        return new InteractiveBrokersNativeCapabilities(
            Array.AsReadOnly(orderTypes),
            SupportedTimeInForce,
            Array.AsReadOnly(new[] { nativeContract.SecurityType }),
            nativeContract.SecurityType,
            minimum,
            increment,
            ToScaledPrice(selected.MinTick),
            !string.IsNullOrWhiteSpace(selected.TradingHours) &&
                !string.Equals(selected.TradingHours, selected.LiquidHours, StringComparison.Ordinal),
            tradingHoursSchedule,
            regularHours,
            selected.TradingHours ?? string.Empty,
            selected.LiquidHours ?? string.Empty,
            DateTime.UtcNow);
    }

    private static BrokerTradingHours ParseDatedTradingHours(
        string datedHours,
        string timeZoneId,
        string scheduleName)
    {
        if (string.IsNullOrWhiteSpace(datedHours) || string.IsNullOrWhiteSpace(timeZoneId))
            throw new InvalidDataException($"IB contract details omitted {scheduleName} or time-zone data.");
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidDataException($"IB returned unsupported time zone '{timeZoneId}'.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidDataException($"IB returned invalid time zone '{timeZoneId}'.", exception);
        }

        var intervalsByDate = new Dictionary<DateOnly, List<BrokerWeeklyTradingInterval>>();
        var closedDates = new HashSet<DateOnly>();
        var closures = new List<BrokerTradingClosure>();
        foreach (var entry in datedHours.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.EndsWith(":CLOSED", StringComparison.Ordinal))
            {
                var dateText = entry[..entry.IndexOf(':')];
                if (!DateOnly.TryParseExact(
                        dateText,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var closedDate) ||
                    intervalsByDate.ContainsKey(closedDate))
                {
                    throw new InvalidDataException($"IB {scheduleName} data contains a conflicting or malformed closed date.");
                }
                closedDates.Add(closedDate);
                AddClosure(closedDate, timeZone, closures);
                continue;
            }

            var parsedRange = false;
            foreach (var range in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = range.IndexOf('-');
                if (separator <= 0 || separator == range.Length - 1)
                    throw new InvalidDataException($"IB {scheduleName} data contains a malformed session range.");
                if (!TryParseIbLocalDateTime(range[..separator], null, out var startLocal) ||
                    !TryParseIbLocalDateTime(range[(separator + 1)..], DateOnly.FromDateTime(startLocal), out var endLocal))
                {
                    throw new InvalidDataException($"IB {scheduleName} data contains an unparseable session range.");
                }
                var sourceDate = DateOnly.FromDateTime(startLocal);
                if (closedDates.Contains(sourceDate))
                    throw new InvalidDataException($"IB {scheduleName} data marks one date both open and closed.");
                var startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), timeZone);
                var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), timeZone);
                if (endUtc <= startUtc || endUtc - startUtc > TimeSpan.FromDays(7))
                    throw new InvalidDataException($"IB {scheduleName} data contains an invalid session duration.");
                if (!intervalsByDate.TryGetValue(sourceDate, out var dateIntervals))
                {
                    dateIntervals = [];
                    intervalsByDate.Add(sourceDate, dateIntervals);
                }
                AddWeeklyInterval(startUtc, endUtc, dateIntervals);
                parsedRange = true;
            }
            if (!parsedRange)
                throw new InvalidDataException($"IB {scheduleName} data contains an empty open session.");
        }

        var weekdayPatterns = new Dictionary<DayOfWeek, BrokerWeeklyTradingInterval[]>();
        foreach (var (date, dateIntervals) in intervalsByDate.OrderBy(static item => item.Key))
        {
            var pattern = dateIntervals
                .Distinct()
                .OrderBy(static item => item.StartSecond)
                .ThenBy(static item => item.EndSecond)
                .ToArray();
            if (pattern.Length == 0)
                throw new InvalidDataException($"IB {scheduleName} data contains an empty dated pattern.");
            if (weekdayPatterns.TryGetValue(date.DayOfWeek, out var existingPattern) &&
                !existingPattern.SequenceEqual(pattern))
            {
                throw new InvalidDataException(
                    $"IB {scheduleName} data contains conflicting repeated {date.DayOfWeek} patterns; " +
                    "dated early-close or DST changes cannot be widened into a recurring schedule.");
            }
            weekdayPatterns[date.DayOfWeek] = pattern;
        }

        var exactIntervals = weekdayPatterns.Values
            .SelectMany(static pattern => pattern)
            .Distinct()
            .OrderBy(static item => item.StartSecond)
            .ThenBy(static item => item.EndSecond)
            .ToArray();
        if (exactIntervals.Length == 0)
            throw new InvalidDataException($"IB {scheduleName} data contained no usable session.");
        var exactClosures = closures.Distinct().OrderBy(static item => item.DateUtc).ThenBy(static item => item.StartSecond).ToArray();
        return BrokerTradingHours.FromWeeklyIntervals(exactIntervals, exactClosures);
    }

    private static void AddWeeklyInterval(
        DateTime startUtc,
        DateTime endUtc,
        ICollection<BrokerWeeklyTradingInterval> target)
    {
        var startSecond = checked((uint)(((int)startUtc.DayOfWeek * BrokerWeeklyTradingInterval.SecondsPerDay) +
            (int)startUtc.TimeOfDay.TotalSeconds));
        var duration = checked((uint)(endUtc - startUtc).TotalSeconds);
        var endSecond = startSecond + duration;
        if (endSecond <= BrokerWeeklyTradingInterval.SecondsPerWeek)
        {
            target.Add(new BrokerWeeklyTradingInterval(startSecond, endSecond));
            return;
        }
        target.Add(new BrokerWeeklyTradingInterval(startSecond, BrokerWeeklyTradingInterval.SecondsPerWeek));
        target.Add(new BrokerWeeklyTradingInterval(0, endSecond - BrokerWeeklyTradingInterval.SecondsPerWeek));
    }

    private static void AddClosure(
        DateOnly localDate,
        TimeZoneInfo timeZone,
        ICollection<BrokerTradingClosure> target)
    {
        var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var localEnd = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
        var cursor = startUtc;
        while (cursor < endUtc)
        {
            var nextMidnight = cursor.Date.AddDays(1);
            var segmentEnd = endUtc < nextMidnight ? endUtc : nextMidnight;
            target.Add(new BrokerTradingClosure(
                DateOnly.FromDateTime(cursor),
                false,
                checked((uint)cursor.TimeOfDay.TotalSeconds),
                segmentEnd == nextMidnight
                    ? BrokerWeeklyTradingInterval.SecondsPerDay
                    : checked((uint)segmentEnd.TimeOfDay.TotalSeconds)));
            cursor = segmentEnd;
        }
    }

    private static bool TryParseIbLocalDateTime(
        string value,
        DateOnly? fallbackDate,
        out DateTime result)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyyMMdd:HHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out result))
        {
            return true;
        }
        if (fallbackDate is { } date && TimeOnly.TryParseExact(
                value,
                "HHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            result = date.ToDateTime(time, DateTimeKind.Unspecified);
            return true;
        }
        result = default;
        return false;
    }

    private static Contract ToNativeContract(InteractiveBrokersContract contract) => new()
    {
        ConId = contract.ContractId,
        Symbol = contract.Symbol,
        SecType = contract.SecurityType,
        Exchange = contract.Exchange,
        PrimaryExch = contract.PrimaryExchange,
        Currency = contract.Currency,
    };

    private static InteractiveBrokersContract FromNativeContract(Contract contract) => new(
        contract.ConId,
        contract.Symbol ?? string.Empty,
        contract.SecType ?? string.Empty,
        contract.Exchange ?? string.Empty,
        contract.PrimaryExch ?? string.Empty,
        contract.Currency ?? string.Empty);

    private static Order ToNativeOrder(InteractiveBrokersOrderRequest request)
    {
        var order = new Order
        {
            OrderId = request.OrderId,
            Account = request.AccountId,
            Action = request.Side,
            TotalQuantity = ToDecimal(request.Quantity),
            OrderType = FormatOrderType(request.OrderType),
            Tif = FormatTimeInForce(request.TimeInForce),
            OrderRef = request.ClientOrderId,
            OutsideRth = request.OutsideRegularTradingHours,
            Transmit = true,
        };
        if (request.LimitPrice is { } limit)
            order.LmtPrice = ToDouble(limit);
        if (request.StopPrice is { } stop)
            order.AuxPrice = ToDouble(stop);
        if (request.TrailStopPrice is { } trailStop)
            order.TrailStopPrice = ToDouble(trailStop);
        if (request.TrailingPercent is { } trailingPercent)
            order.TrailingPercent = ToDouble(trailingPercent);
        return order;
    }

    private static void ValidateContract(InteractiveBrokersContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.ContractId < 0 ||
            string.IsNullOrWhiteSpace(contract.Symbol) || contract.Symbol.Length > 64 ||
            string.IsNullOrWhiteSpace(contract.SecurityType) || contract.SecurityType.Length > 16 ||
            string.IsNullOrWhiteSpace(contract.Exchange) || contract.Exchange.Length > 32 ||
            string.IsNullOrWhiteSpace(contract.Currency) || contract.Currency.Length > 8 ||
            contract.PrimaryExchange.Length > 32)
        {
            throw new ArgumentException("The Interactive Brokers contract identity is invalid.", nameof(contract));
        }
    }

    private static void ValidateOrderRequest(InteractiveBrokersOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateContract(request.Contract);
        if (request.OrderId < 0 ||
            string.IsNullOrWhiteSpace(request.ClientOrderId) || request.ClientOrderId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.AccountId) || request.AccountId.Length > 128 ||
            request.Side is not ("BUY" or "SELL") ||
            request.OrderType == InteractiveBrokersNativeOrderType.Unknown ||
            request.TimeInForce == InteractiveBrokersNativeTimeInForce.Unknown ||
            !request.Quantity.IsValid || request.Quantity.Coefficient <= 0 ||
            request.LimitPrice is { IsValid: false } ||
            request.StopPrice is { IsValid: false } ||
            request.TrailStopPrice is { IsValid: false } ||
            request.TrailingPercent is { IsValid: false })
        {
            throw new ArgumentException("The Interactive Brokers order request is invalid.", nameof(request));
        }
    }

    private static string ResolveAuthenticatedAccount(IReadOnlyList<string> accounts, string expectedAccountId)
    {
        if (accounts.Count == 0)
            throw new InvalidOperationException("TWS or IB Gateway returned no managed account identity.");
        if (string.IsNullOrEmpty(expectedAccountId))
            return accounts[0];
        if (!accounts.Contains(expectedAccountId, StringComparer.Ordinal))
            throw new InvalidOperationException("The authenticated TWS account does not match the configured account ID.");
        return expectedAccountId;
    }

    private static string FormatOrderType(InteractiveBrokersNativeOrderType value) => value switch
    {
        InteractiveBrokersNativeOrderType.Market => "MKT",
        InteractiveBrokersNativeOrderType.Limit => "LMT",
        InteractiveBrokersNativeOrderType.Stop => "STP",
        InteractiveBrokersNativeOrderType.StopLimit => "STP LMT",
        InteractiveBrokersNativeOrderType.TrailingStop => "TRAIL",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static InteractiveBrokersNativeOrderType ParseOrderType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MKT" => InteractiveBrokersNativeOrderType.Market,
        "LMT" => InteractiveBrokersNativeOrderType.Limit,
        "STP" => InteractiveBrokersNativeOrderType.Stop,
        "STP LMT" => InteractiveBrokersNativeOrderType.StopLimit,
        "TRAIL" => InteractiveBrokersNativeOrderType.TrailingStop,
        _ => InteractiveBrokersNativeOrderType.Unknown,
    };

    private static string FormatTimeInForce(InteractiveBrokersNativeTimeInForce value) => value switch
    {
        InteractiveBrokersNativeTimeInForce.Day => "DAY",
        InteractiveBrokersNativeTimeInForce.GoodTillCancelled => "GTC",
        InteractiveBrokersNativeTimeInForce.ImmediateOrCancel => "IOC",
        InteractiveBrokersNativeTimeInForce.FillOrKill => "FOK",
        InteractiveBrokersNativeTimeInForce.MarketOnOpen => "OPG",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static InteractiveBrokersNativeTimeInForce ParseTimeInForce(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "DAY" => InteractiveBrokersNativeTimeInForce.Day,
        "GTC" => InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
        "IOC" => InteractiveBrokersNativeTimeInForce.ImmediateOrCancel,
        "FOK" => InteractiveBrokersNativeTimeInForce.FillOrKill,
        "OPG" => InteractiveBrokersNativeTimeInForce.MarketOnOpen,
        _ => InteractiveBrokersNativeTimeInForce.Unknown,
    };

    private static InteractiveBrokersNativeOrderStatus ParseOrderStatus(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "APIPENDING" or "PENDINGSUBMIT" => InteractiveBrokersNativeOrderStatus.PendingSubmit,
        "PRESUBMITTED" => InteractiveBrokersNativeOrderStatus.PreSubmitted,
        "SUBMITTED" => InteractiveBrokersNativeOrderStatus.Submitted,
        "PENDINGCANCEL" => InteractiveBrokersNativeOrderStatus.PendingCancel,
        "CANCELLED" => InteractiveBrokersNativeOrderStatus.Cancelled,
        "FILLED" => InteractiveBrokersNativeOrderStatus.Filled,
        "INACTIVE" => InteractiveBrokersNativeOrderStatus.Inactive,
        "APICANCELLED" => InteractiveBrokersNativeOrderStatus.ApiCancelled,
        "REJECTED" => InteractiveBrokersNativeOrderStatus.Rejected,
        _ => InteractiveBrokersNativeOrderStatus.Unknown,
    };

    private static bool OrderUsesStopPrice(string? value) =>
        string.Equals(value, "STP", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "STP LMT", StringComparison.OrdinalIgnoreCase);

    private static decimal ToDecimal(ScaledQuantity value) =>
        value.Coefficient / Pow10Decimal(value.Scale);

    private static double ToDouble(ScaledPrice value) =>
        ToExactDouble(value.Coefficient, value.Scale, "price");

    private static double ToDouble(ScaledRatio value) =>
        ToExactDouble(value.Coefficient, value.Scale, "ratio");

    private static double ToExactDouble(long coefficient, byte scale, string valueKind)
    {
        var exact = coefficient / Pow10Decimal(scale);
        var native = (double)exact;
        decimal roundTrip;
        try
        {
            roundTrip = (decimal)native;
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"The exact IB {valueKind} cannot be represented by the native double API.",
                exception);
        }
        if (roundTrip != exact)
        {
            throw new InvalidOperationException(
                $"The exact IB {valueKind} would change when represented by the native double API.");
        }
        return native;
    }

    private static decimal Pow10Decimal(byte scale)
    {
        decimal value = 1;
        for (var index = 0; index < scale; index++)
            value *= 10;
        return value;
    }

    private static ScaledQuantity ToScaledQuantity(decimal value)
    {
        var text = value.ToString("G29", CultureInfo.InvariantCulture);
        if (!TryParseScaled(text, out var coefficient, out var scale))
            throw new InvalidDataException("IB returned an unrepresentable exact quantity.");
        return new ScaledQuantity(coefficient, scale);
    }

    private static ScaledPrice ToScaledPrice(double value)
    {
        if (!TryConvertDouble(value, out var coefficient, out var scale))
            throw new InvalidDataException("IB returned an unrepresentable exact price.");
        return new ScaledPrice(coefficient, scale);
    }

    private static ScaledMoney ToScaledMoney(double value)
    {
        if (!TryConvertDouble(value, out var coefficient, out var scale))
            throw new InvalidDataException("IB returned an unrepresentable exact money value.");
        return new ScaledMoney(coefficient, scale);
    }

    private static ScaledPrice? OptionalPrice(double value) =>
        double.IsFinite(value) && value != double.MaxValue && value != 0
            ? ToScaledPrice(value)
            : null;

    private static ScaledRatio? OptionalRatio(double value)
    {
        if (!double.IsFinite(value) || value == double.MaxValue || value == 0)
            return null;
        if (!TryConvertDouble(value, out var coefficient, out var scale))
            throw new InvalidDataException("IB returned an unrepresentable trailing percentage.");
        return new ScaledRatio(coefficient, scale);
    }

    private static bool TryConvertDouble(double value, out long coefficient, out byte scale)
    {
        coefficient = 0;
        scale = 0;
        if (!double.IsFinite(value) || value is > (double)decimal.MaxValue or < (double)decimal.MinValue)
            return false;
        var exactDecimal = (decimal)value;
        return TryParseScaled(exactDecimal.ToString("G29", CultureInfo.InvariantCulture), out coefficient, out scale);
    }

    private static bool TryParseScaled(string? value, out long coefficient, out byte scale)
    {
        coefficient = 0;
        scale = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var span = value.AsSpan().Trim();
        var negative = false;
        var index = 0;
        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            index++;
        }
        if (index == span.Length)
            return false;
        Int128 magnitude = 0;
        var decimals = 0;
        var seenDecimal = false;
        var seenDigit = false;
        for (; index < span.Length; index++)
        {
            var character = span[index];
            if (character == '.' && !seenDecimal)
            {
                seenDecimal = true;
                continue;
            }
            if (character is < '0' or > '9')
                return false;
            seenDigit = true;
            if (seenDecimal)
                decimals++;
            if (decimals > ScaledValueMath.MaximumScale || magnitude > (Int128.MaxValue - 9) / 10)
                return false;
            magnitude = magnitude * 10 + (character - '0');
        }
        if (!seenDigit)
            return false;
        var signed = negative ? -magnitude : magnitude;
        return ScaledValueMath.TryNarrow(signed, decimals, out coefficient, out scale);
    }

    private static bool TrySubtract(
        ScaledQuantity left,
        ScaledQuantity right,
        out ScaledQuantity result)
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
            !ScaledValueMath.TryNarrow(alignedLeft - alignedRight, scale, out var coefficient, out var resultScale))
        {
            return false;
        }
        result = new ScaledQuantity(coefficient, resultScale);
        return true;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void Raise<T>(Action<T>? handler, T value)
    {
        try
        {
            handler?.Invoke(value);
        }
        catch (Exception exception)
        {
            ReportFault(new InvalidOperationException("An IB transport event subscriber faulted.", exception));
        }
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            Faulted?.Invoke(exception);
        }
        catch
        {
            // A diagnostic subscriber cannot terminate the one native reader loop.
        }
    }

    private sealed class PendingContractDetails(InteractiveBrokersContract requestedContract)
    {
        internal InteractiveBrokersContract RequestedContract { get; } = requestedContract;

        internal List<ContractDetails> Details { get; } = [];

        internal TaskCompletionSource<InteractiveBrokersNativeCapabilities> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CashAccumulator
    {
        internal ScaledMoney? TotalCash { get; set; }

        internal ScaledMoney? AvailableFunds { get; set; }
    }

    private sealed class ReconciliationRequest(
        string accountId,
        int accountSummaryRequestId,
        int maximumRows)
    {
        private readonly int _maximumRows = maximumRows;

        internal string AccountId { get; } = accountId;

        internal int AccountSummaryRequestId { get; } = accountSummaryRequestId;

        internal Dictionary<int, InteractiveBrokersOrderSnapshot> OpenOrders { get; } = [];

        internal Dictionary<int, InteractiveBrokersOrderSnapshot> CompletedOrders { get; } = [];

        internal Dictionary<(int ContractId, string Symbol), InteractiveBrokersPositionSnapshot> Positions { get; } = [];

        internal Dictionary<string, CashAccumulator> Cash { get; } = new(StringComparer.Ordinal);

        internal bool OpenOrdersComplete { get; set; }

        internal bool CompletedOrdersComplete { get; set; }

        internal bool PositionsComplete { get; set; }

        internal bool CashComplete { get; set; }

        internal TaskCompletionSource<InteractiveBrokersReconciliationSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void RememberOpenOrder(InteractiveBrokersOrderSnapshot snapshot)
        {
            if (CanAdd(OpenOrders, snapshot.OrderId, "open orders"))
                OpenOrders[snapshot.OrderId] = snapshot;
        }

        internal void RememberCompletedOrder(InteractiveBrokersOrderSnapshot snapshot)
        {
            if (CanAdd(CompletedOrders, snapshot.OrderId, "completed orders"))
                CompletedOrders[snapshot.OrderId] = snapshot;
        }

        internal void RememberPosition(InteractiveBrokersPositionSnapshot snapshot)
        {
            var key = (snapshot.Contract.ContractId, snapshot.Contract.Symbol);
            if (CanAdd(Positions, key, "positions"))
                Positions[key] = snapshot;
        }

        internal void RememberCash(string currency, string tag, ScaledMoney value)
        {
            if (!string.Equals(tag, "TotalCashValue", StringComparison.Ordinal) &&
                !string.Equals(tag, "AvailableFunds", StringComparison.Ordinal))
            {
                return;
            }
            if (!Cash.TryGetValue(currency, out var cash))
            {
                if (!CanAdd(Cash, currency, "cash rows"))
                    return;
                cash = new CashAccumulator();
                Cash.Add(currency, cash);
            }
            if (string.Equals(tag, "TotalCashValue", StringComparison.Ordinal))
                cash.TotalCash = value;
            else
                cash.AvailableFunds = value;
        }

        internal void TryComplete()
        {
            if (!OpenOrdersComplete || !CompletedOrdersComplete || !PositionsComplete || !CashComplete)
                return;
            var capturedAt = DateTime.UtcNow;
            var cash = Cash
                .Where(static pair => pair.Value.TotalCash.HasValue && pair.Value.AvailableFunds.HasValue)
                .Select(pair => new InteractiveBrokersCashSnapshot(
                    AccountId,
                    pair.Key,
                    pair.Value.TotalCash!.Value,
                    pair.Value.AvailableFunds!.Value,
                    capturedAt))
                .ToArray();
            Completion.TrySetResult(new InteractiveBrokersReconciliationSnapshot(
                AccountId,
                Array.AsReadOnly(OpenOrders.Values.OrderBy(static item => item.OrderId).ToArray()),
                Array.AsReadOnly(CompletedOrders.Values.OrderBy(static item => item.OrderId).ToArray()),
                Array.AsReadOnly(Positions.Values.OrderBy(static item => item.Contract.ContractId).ToArray()),
                Array.AsReadOnly(cash),
                capturedAt));
        }

        private bool CanAdd<TKey, TValue>(
            IReadOnlyDictionary<TKey, TValue> rows,
            TKey key,
            string category)
            where TKey : notnull
        {
            if (Completion.Task.IsCompleted)
                return false;
            if (rows.ContainsKey(key))
                return true;
            if (rows.Count < _maximumRows)
                return true;
            Completion.TrySetException(new InvalidDataException(
                $"The IB reconciliation {category} snapshot exceeded its configured {_maximumRows}-row bound."));
            return false;
        }
    }
}
#endif
