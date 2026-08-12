using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.CTrader;

/// <summary>Strongly typed cTrader trading-account identity.</summary>
public readonly record struct CTraderAccountId(long Value)
{
    /// <summary>Gets whether the native account identity is usable.</summary>
    public bool IsValid => Value > 0;
}

/// <summary>Strongly typed cTrader symbol identity.</summary>
public readonly record struct CTraderSymbolId(long Value)
{
    /// <summary>Gets whether the native symbol identity is usable.</summary>
    public bool IsValid => Value > 0;
}

/// <summary>Strongly typed cTrader native order identity.</summary>
public readonly record struct CTraderNativeOrderId(long Value)
{
    /// <summary>Gets whether the native order identity is usable.</summary>
    public bool IsValid => Value > 0;
}

/// <summary>Fail-closed cTrader connection/authentication result.</summary>
public enum CTraderConnectionFault : byte
{
    /// <summary>The selected execution session and initial reconciliation snapshot are ready.</summary>
    None = 0,

    /// <summary>The explicit execution opt-in was not enabled.</summary>
    Disabled = 1,

    /// <summary>Required local configuration is incomplete or invalid.</summary>
    InvalidConfiguration = 2,

    /// <summary>OAuth credentials or the account ID are absent.</summary>
    MissingCredentials = 3,

    /// <summary>The TLS/protobuf transport could not be established.</summary>
    TransportFailure = 4,

    /// <summary>Spotware rejected application authentication.</summary>
    ApplicationAuthenticationFailed = 5,

    /// <summary>The peer did not report Open API protocol version 2.0.</summary>
    ProtocolVersionMismatch = 6,

    /// <summary>The configured account was absent, live, or could not be authenticated.</summary>
    AccountAuthenticationFailed = 7,

    /// <summary>The authenticated account is data-only or otherwise cannot submit new orders.</summary>
    DataOnlyAccount = 8,

    /// <summary>Exact symbol capabilities could not be certified.</summary>
    CapabilityDiscoveryFailed = 9,

    /// <summary>The initial broker snapshot could not be completed coherently.</summary>
    ReconciliationFailed = 10,
}

/// <summary>Immutable connection result with the observed execution/data authorization state.</summary>
public readonly record struct CTraderConnectionResult(
    CTraderConnectionFault Fault,
    BrokerExecutionSession Session,
    string? Reason = null)
{
    /// <summary>Gets whether the account is ready for exact execution in its selected mode.</summary>
    public bool IsSuccess => Fault == CTraderConnectionFault.None && Session.CanExecute;
}

/// <summary>Fail-closed reconciliation refresh result.</summary>
public enum CTraderSnapshotFault : byte
{
    /// <summary>The immutable cache was refreshed coherently.</summary>
    None = 0,

    /// <summary>No authenticated transport is available.</summary>
    Disconnected = 1,

    /// <summary>A request, response, or account identity was invalid.</summary>
    ProtocolFailure = 2,

    /// <summary>A broker value could not be represented exactly by the execution seam.</summary>
    UnrepresentableValue = 3,

    /// <summary>The completed-order response declared that it was incomplete.</summary>
    IncompleteSnapshot = 4,
}

/// <summary>Result of one asynchronous refresh into the synchronous reconciliation cache.</summary>
public readonly record struct CTraderSnapshotRefreshResult(
    CTraderSnapshotFault Fault,
    BrokerReconciliationSnapshot Snapshot,
    string? Reason = null)
{
    /// <summary>Gets whether the new snapshot replaced the prior cache.</summary>
    public bool IsSuccess => Fault == CTraderSnapshotFault.None;
}

/// <summary>
/// Spotware Open API 2.0 execution adapter. One instance is intentionally bound to one
/// exact account, one mode, and one canonical instrument because the capability seam is account-wide.
/// </summary>
public sealed class CTraderExecutionAdapter : IBrokerExecutionAdapter, IAsyncDisposable
{
    private const string RequiredProtocolVersion = "2.0";
    private const int MaximumClientOrderIdLength = 50;
    private readonly object _gate = new();
    private readonly CTraderExecutionOptions _options;
    private readonly CTraderExecutionEndpoint _endpoint;
    private readonly ILiveExecutionConfirmationStore? _liveConfirmationStore;
    private readonly ICTraderExecutionTransport _transport;
    private readonly IClock _clock;
    private readonly IAdapterEventScheduler _scheduler;
    private readonly CTraderSerializedEventScheduler? _faultReportingScheduler;
    private readonly Dictionary<string, TaskCompletionSource<object>> _pendingRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCommandCorrelation> _pendingCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientOrderId, TrackedOrder> _orders = [];
    private readonly Dictionary<BrokerOrderId, ClientOrderId> _brokerToClient = [];
    private BrokerExecutionSession _session;
    private BrokerExecutionCapabilities _capabilities;
    private BrokerReconciliationSnapshot _snapshot;
    private DateTime _rateWindowStartedUtc;
    private int _commandsInRateWindow;
    private byte _priceDigits;
    private bool _disposed;

    /// <summary>Creates a fail-closed adapter over an injected real or in-process transport.</summary>
    public CTraderExecutionAdapter(
        CTraderExecutionOptions options,
        ICTraderExecutionTransport transport,
        IClock clock,
        IAdapterEventScheduler scheduler,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Snapshot();
        if (!_options.Enabled)
            throw new InvalidOperationException("CTrader execution must be explicitly enabled before constructing the adapter.");
        _liveConfirmationStore = confirmationStore;
        _endpoint = CTraderExecutionEndpointGate.Resolve(_options, confirmationStore);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (_transport.Endpoint != _endpoint || !_transport.Endpoint.IsAuthorized)
            throw new InvalidOperationException("The injected cTrader transport does not identify the exact gated endpoint.");
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _faultReportingScheduler = scheduler as CTraderSerializedEventScheduler;
        if (_faultReportingScheduler is not null)
            _faultReportingScheduler.CallbackFaulted += OnSchedulerCallbackFault;

        NativeAccountId = new CTraderAccountId(_options.CtidTraderAccountId);
        NativeSymbolId = new CTraderSymbolId(_options.SymbolId);
        Instrument = new InstrumentId(_options.CanonicalInstrumentId);
        Account = new BrokerExecutionAccount(
            new ExecutionAdapterId(Mode == ExecutionMode.Live ? "ctrader-openapi-live" : "ctrader-openapi-demo"),
            new BrokerAccountId(NativeAccountId.IsValid
                ? NativeAccountId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"unconfigured-{EnvironmentLabel.ToLowerInvariant()}-account"));
        var now = UtcNow();
        _session = new BrokerExecutionSession(
            Account,
            ExecutionSessionHealth.Disconnected,
            IsDataConnected: false,
            IsExecutionAuthenticated: false,
            IsExecutionCertified: false,
            now);
        _capabilities = UnavailableCapabilities();
        _snapshot = EmptySnapshot(DateTime.UnixEpoch);
        _rateWindowStartedUtc = now;
        _transport.MessageReceived += OnTransportMessage;
        _transport.Faulted += OnTransportFault;
    }

    /// <inheritdoc />
    public string BrokerId => CTraderExecutionOptions.BrokerId;

    /// <inheritdoc />
    public ExecutionMode Mode => _endpoint.Mode;

    private string EnvironmentLabel => Mode == ExecutionMode.Live ? "LIVE" : "DEMO";

    /// <summary>The configured native account identity.</summary>
    public CTraderAccountId NativeAccountId { get; }

    /// <summary>The configured native symbol identity.</summary>
    public CTraderSymbolId NativeSymbolId { get; }

    /// <summary>The sole canonical instrument certified by this adapter instance.</summary>
    public InstrumentId Instrument { get; }

    /// <inheritdoc />
    public BrokerExecutionAccount Account { get; }

    /// <inheritdoc />
    public BrokerExecutionSession Session
    {
        get
        {
            lock (_gate)
                return _session;
        }
    }

    /// <inheritdoc />
    public BrokerExecutionCapabilities Capabilities
    {
        get
        {
            lock (_gate)
                return _capabilities;
        }
    }

    /// <inheritdoc />
    public event Action<BrokerAdapterEvent>? EventReceived;

    /// <summary>
    /// Raised from the injected scheduler after a trader, margin, or cash-flow message produces a
    /// fresh coherent cash snapshot. Cash is account-scoped and therefore does not invent an order
    /// identity merely to enter the order-event seam.
    /// </summary>
    public event Action<BrokerCashSnapshot>? CashSnapshotReceived;

    /// <summary>
    /// Performs application auth, protocol negotiation, explicit environment proof, account auth,
    /// capability discovery, and an initial coherent reconciliation refresh.
    /// </summary>
    public async Task<CTraderConnectionResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_options.Enabled)
            return ConnectionFailure(CTraderConnectionFault.Disabled, "The explicit cTrader execution opt-in is disabled.");
        var configurationFault = _options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            return ConnectionFailure(CTraderConnectionFault.InvalidConfiguration, configurationFault);
        if (!_options.HasRequiredCredentials || !NativeAccountId.IsValid || !NativeSymbolId.IsValid || Instrument.IsNone)
        {
            return ConnectionFailure(
                CTraderConnectionFault.MissingCredentials,
                $"OAuth credentials, a {EnvironmentLabel} account ID, and the one-symbol binding are required from local configuration.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
        {
            CloseUnauthorizedSession();
            return ConnectionFailure(CTraderConnectionFault.InvalidConfiguration, authorizationFault!);
        }

        var stage = CTraderConnectionFault.TransportFailure;
        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            SetSession(ExecutionSessionHealth.Degraded, true, false, false);

            stage = CTraderConnectionFault.ApplicationAuthenticationFailed;
            _ = await RequestAsync<ProtoOAApplicationAuthRes>(
                new ProtoOAApplicationAuthReq
                {
                    ClientId = _options.ClientId,
                    ClientSecret = _options.ClientSecret,
                },
                cancellationToken).ConfigureAwait(false);

            stage = CTraderConnectionFault.ProtocolVersionMismatch;
            var version = await RequestAsync<ProtoOAVersionRes>(
                new ProtoOAVersionReq(),
                cancellationToken).ConfigureAwait(false);
            if (!version.HasVersion || !string.Equals(version.Version, RequiredProtocolVersion, StringComparison.Ordinal))
            {
                return await DisconnectFailureAsync(
                    CTraderConnectionFault.ProtocolVersionMismatch,
                    $"The cTrader peer reported protocol '{version.Version}', not required Open API 2.0.").ConfigureAwait(false);
            }

            stage = CTraderConnectionFault.AccountAuthenticationFailed;
            var accountList = await RequestAsync<ProtoOAGetAccountListByAccessTokenRes>(
                new ProtoOAGetAccountListByAccessTokenReq { AccessToken = _options.AccessToken },
                cancellationToken).ConfigureAwait(false);
            var account = accountList.CtidTraderAccount.FirstOrDefault(item =>
                item.CtidTraderAccountId == (ulong)NativeAccountId.Value);
            var expectsLiveAccount = Mode == ExecutionMode.Live;
            if (account is null || !account.HasIsLive || account.IsLive != expectsLiveAccount)
            {
                return await DisconnectFailureAsync(
                    CTraderConnectionFault.AccountAuthenticationFailed,
                    $"The configured account was not explicitly proved to be a {EnvironmentLabel} account for this token.").ConfigureAwait(false);
            }

            var accountAuth = await RequestAsync<ProtoOAAccountAuthRes>(
                new ProtoOAAccountAuthReq
                {
                    CtidTraderAccountId = NativeAccountId.Value,
                    AccessToken = _options.AccessToken,
                },
                cancellationToken).ConfigureAwait(false);
            if (accountAuth.CtidTraderAccountId != NativeAccountId.Value)
            {
                return await DisconnectFailureAsync(
                    CTraderConnectionFault.AccountAuthenticationFailed,
                    "The authenticated cTrader account identity did not match configuration.").ConfigureAwait(false);
            }

            stage = CTraderConnectionFault.CapabilityDiscoveryFailed;
            var traderResponse = await RequestAsync<ProtoOATraderRes>(
                new ProtoOATraderReq { CtidTraderAccountId = NativeAccountId.Value },
                cancellationToken).ConfigureAwait(false);
            if (traderResponse.CtidTraderAccountId != NativeAccountId.Value ||
                traderResponse.Trader is null ||
                traderResponse.Trader.CtidTraderAccountId != NativeAccountId.Value)
                throw new CTraderProtocolException("The trader capability response was missing or belonged to another account.");

            var symbolRequest = new ProtoOASymbolByIdReq { CtidTraderAccountId = NativeAccountId.Value };
            symbolRequest.SymbolId.Add(NativeSymbolId.Value);
            var symbolResponse = await RequestAsync<ProtoOASymbolByIdRes>(symbolRequest, cancellationToken).ConfigureAwait(false);
            if (symbolResponse.CtidTraderAccountId != NativeAccountId.Value)
                throw new CTraderProtocolException("The symbol capability response belonged to another account.");
            var symbol = symbolResponse.Symbol.SingleOrDefault(item => item.SymbolId == NativeSymbolId.Value)
                ?? throw new CTraderProtocolException("The configured symbol was absent from capability discovery.");
            var discovered = DiscoverCapabilities(symbol);
            lock (_gate)
            {
                _capabilities = discovered;
                _priceDigits = (byte)symbol.Digits;
            }

            var fullAccess =
                accountList.HasPermissionScope &&
                accountList.PermissionScope == ProtoOAClientPermissionScope.ScopeTrade &&
                traderResponse.Trader.HasAccessRights &&
                traderResponse.Trader.AccessRights == ProtoOAAccessRights.FullAccess;
            SetSession(
                ExecutionSessionHealth.Degraded,
                isDataConnected: true,
                isExecutionAuthenticated: fullAccess,
                isExecutionCertified: false);

            stage = CTraderConnectionFault.ReconciliationFailed;
            var refresh = await RefreshReconciliationAsync(cancellationToken).ConfigureAwait(false);
            if (!refresh.IsSuccess)
            {
                return await DisconnectFailureAsync(
                    CTraderConnectionFault.ReconciliationFailed,
                    refresh.Reason ?? refresh.Fault.ToString()).ConfigureAwait(false);
            }

            if (!fullAccess)
            {
                SetSession(ExecutionSessionHealth.Healthy, true, false, false);
                return new CTraderConnectionResult(
                    CTraderConnectionFault.DataOnlyAccount,
                    Session,
                    $"The {EnvironmentLabel} account is connected for data but does not have full execution rights.");
            }
            SetSession(ExecutionSessionHealth.Healthy, true, true, true);
            return new CTraderConnectionResult(CTraderConnectionFault.None, Session);
        }
        catch (OperationCanceledException)
        {
            await SafeDisconnectAsync().ConfigureAwait(false);
            SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
            throw;
        }
        catch (Exception exception)
        {
            return await DisconnectFailureAsync(stage, SafeReason(exception)).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the session and revokes execution admission.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        FailPendingRequests(new InvalidOperationException("The cTrader execution session was disconnected."));
        MarkPendingCommandsUnknown();
    }

    /// <inheritdoc />
    public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command)
    {
        if (command is null || command.Instruction is null || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The submit command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "cTrader LIVE submit requires a current one-use OMS guardrail admission.");
        }
        if (command.Instruction.TradeIntent.Instrument != Instrument)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "This adapter is certified for one configured instrument only.");
        if (command.Instruction.Identity.ClientOrderId.Value.Length > MaximumClientOrderIdLength)
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, "The client order ID exceeds cTrader's 50-character limit.");

        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, command.Instruction, UtcNow());
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local cTrader command budget is exhausted.");
        if (!TryCreateNewOrder(command.Instruction, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);

        var clientOrderId = command.Instruction.Identity.ClientOrderId;
        lock (_gate)
        {
            if (_orders.ContainsKey(clientOrderId))
            {
                return new BrokerAdapterCommandResult(
                    BrokerAdapterCommandStatus.Conflict,
                    BrokerAdapterCommandFault.Conflict,
                    null,
                    0,
                    "The client order ID is already bound to a cTrader order.");
            }
            _orders.Add(clientOrderId, new TrackedOrder(
                command.Instruction,
                command.Instruction.Terms,
                OrderLifecycleState.Acknowledging,
                null,
                ScaledQuantity.Zero,
                command.CausationId,
                BrokerAdapterCommandKind.Submit,
                null));
        }

        SendCommand(request!, BrokerAdapterCommandKind.Submit, clientOrderId, command.CausationId);
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Submit, clientOrderId, command.CausationId));
    }

    /// <inheritdoc />
    public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid)
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The cancel command is invalid.");
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "cTrader LIVE cancel requires a current one-use OMS guardrail admission.");
        }
        if (!Session.CanExecute)
            return Rejected(BrokerAdapterCommandFault.ExecutionUnavailable, "The cTrader execution session is unavailable.");
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) ||
            tracked!.BrokerOrderId is not { } brokerOrderId ||
            !TryParseNativeOrderId(brokerOrderId, out var nativeOrderId))
        {
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "A cTrader broker order ID is not known for the requested order.");
        }
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local cTrader command budget is exhausted.");

        lock (_gate)
        {
            _orders[clientOrderId] = tracked with
            {
                State = OrderLifecycleState.PendingCancel,
                PendingCausationId = command.CausationId,
                PendingCommand = BrokerAdapterCommandKind.Cancel,
            };
        }
        SendCommand(
            new ProtoOACancelOrderReq
            {
                CtidTraderAccountId = NativeAccountId.Value,
                OrderId = nativeOrderId.Value,
            },
            BrokerAdapterCommandKind.Cancel,
            clientOrderId,
            command.CausationId);
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Cancel, clientOrderId, command.CausationId));
    }

    /// <inheritdoc />
    public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
    {
        if (command is null || !command.Order.IsValid || !command.CausationId.IsValid ||
            !string.Equals(command.CapabilityVersion, Capabilities.Version, StringComparison.Ordinal))
        {
            return Rejected(BrokerAdapterCommandFault.InvalidCommand, "The replace command is invalid or uses stale capabilities.");
        }
        if (!TryValidateLiveAuthorization(out var authorizationFault))
            return RejectRevokedLiveAuthorization(authorizationFault!);
        if (Mode == ExecutionMode.Live &&
            !ExecutionCoordinator.TryConsumeLiveGuardrailAdmission(Account, command))
        {
            return Rejected(
                BrokerAdapterCommandFault.ExecutionUnavailable,
                "cTrader LIVE replace requires a current one-use OMS guardrail admission.");
        }
        if (!TryResolve(command.Order, out var clientOrderId, out var tracked) ||
            tracked!.BrokerOrderId is not { } brokerOrderId ||
            !TryParseNativeOrderId(brokerOrderId, out var nativeOrderId))
        {
            return Rejected(BrokerAdapterCommandFault.OrderNotFound, "A cTrader broker order ID is not known for the requested order.");
        }
        if (command.ReplacementTerms.Side != tracked.CurrentTerms.Side ||
            command.ReplacementTerms.OrderType != tracked.CurrentTerms.OrderType ||
            command.ReplacementTerms.TimeInForce != tracked.CurrentTerms.TimeInForce)
        {
            return Rejected(
                BrokerAdapterCommandFault.UnsupportedCapability,
                "cTrader amend cannot change side, order type, or time in force without cancel-and-new.");
        }

        var replacement = tracked.Instruction with { Terms = command.ReplacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(Session, Capabilities, replacement, UtcNow(), isReplace: true);
        if (!admission.IsSuccess)
            return AdmissionRejected(admission);
        if (!TryConsumeRateBudget())
            return Rejected(BrokerAdapterCommandFault.RateLimited, "The local cTrader command budget is exhausted.");
        if (!TryCreateAmendOrder(nativeOrderId, command.ReplacementTerms, out var request, out var reason))
            return Rejected(BrokerAdapterCommandFault.UnsupportedCapability, reason!);

        lock (_gate)
        {
            _orders[clientOrderId] = tracked with
            {
                State = OrderLifecycleState.PendingReplace,
                PendingCausationId = command.CausationId,
                PendingCommand = BrokerAdapterCommandKind.Replace,
                PendingReplacement = command.ReplacementTerms,
            };
        }
        SendCommand(request!, BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId);
        return Dispatched(CreateReceipt(BrokerAdapterCommandKind.Replace, clientOrderId, command.CausationId));
    }

    /// <inheritdoc />
    public BrokerOrderQueryResult Query(BrokerOrderQuery query)
    {
        if (!query.IsValid)
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.InvalidCommand, null, "The order query is invalid.");
        if (!TryResolve(query, out _, out var tracked))
            return new BrokerOrderQueryResult(false, BrokerAdapterCommandFault.OrderNotFound, null);
        return new BrokerOrderQueryResult(true, BrokerAdapterCommandFault.None, ToSnapshot(tracked!));
    }

    /// <inheritdoc />
    public BrokerReconciliationSnapshot CaptureReconciliationSnapshot()
    {
        lock (_gate)
            return CopySnapshot(_snapshot);
    }

    /// <summary>
    /// Refreshes Open API reconcile, completed-order, trader, and asset messages into one immutable
    /// point-in-time cache. No coordinator call performs network I/O synchronously.
    /// </summary>
    public async Task<CTraderSnapshotRefreshResult> RefreshReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_transport.IsConnected)
            return SnapshotFailure(CTraderSnapshotFault.Disconnected, "The cTrader transport is disconnected.");

        try
        {
            var now = UtcNow();
            var reconcile = await RequestAsync<ProtoOAReconcileRes>(
                new ProtoOAReconcileReq
                {
                    CtidTraderAccountId = NativeAccountId.Value,
                },
                cancellationToken).ConfigureAwait(false);
            var orderList = await RequestAsync<ProtoOAOrderListRes>(
                new ProtoOAOrderListReq
                {
                    CtidTraderAccountId = NativeAccountId.Value,
                    FromTimestamp = new DateTimeOffset(now.AddDays(-_options.CompletedOrderLookbackDays)).ToUnixTimeMilliseconds(),
                    ToTimestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                },
                cancellationToken).ConfigureAwait(false);
            if (orderList.HasMore)
            {
                return SnapshotFailure(
                    CTraderSnapshotFault.IncompleteSnapshot,
                    "cTrader reported that the bounded completed-order response was incomplete.");
            }
            var traderResponse = await RequestAsync<ProtoOATraderRes>(
                new ProtoOATraderReq { CtidTraderAccountId = NativeAccountId.Value },
                cancellationToken).ConfigureAwait(false);
            var assets = await RequestAsync<ProtoOAAssetListRes>(
                new ProtoOAAssetListReq { CtidTraderAccountId = NativeAccountId.Value },
                cancellationToken).ConfigureAwait(false);

            if (reconcile.CtidTraderAccountId != NativeAccountId.Value ||
                orderList.CtidTraderAccountId != NativeAccountId.Value ||
                traderResponse.CtidTraderAccountId != NativeAccountId.Value ||
                assets.CtidTraderAccountId != NativeAccountId.Value ||
                traderResponse.Trader is null)
            {
                return SnapshotFailure(CTraderSnapshotFault.ProtocolFailure, "A reconciliation response belonged to another account.");
            }

            if (!TryBuildSnapshot(reconcile, orderList, traderResponse.Trader, assets, now, out var snapshot, out var reason))
                return SnapshotFailure(CTraderSnapshotFault.UnrepresentableValue, reason!);

            lock (_gate)
                _snapshot = snapshot!;
            return new CTraderSnapshotRefreshResult(CTraderSnapshotFault.None, CopySnapshot(snapshot!));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SnapshotFailure(CTraderSnapshotFault.ProtocolFailure, SafeReason(exception));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _transport.MessageReceived -= OnTransportMessage;
        _transport.Faulted -= OnTransportFault;
        if (_faultReportingScheduler is not null)
            _faultReportingScheduler.CallbackFaulted -= OnSchedulerCallbackFault;
        FailPendingRequests(new ObjectDisposedException(nameof(CTraderExecutionAdapter)));
        MarkPendingCommandsUnknown();
        await _transport.DisposeAsync().ConfigureAwait(false);
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
    }

    private BrokerExecutionCapabilities DiscoverCapabilities(ProtoOASymbol symbol)
    {
        if (symbol.SymbolId != NativeSymbolId.Value ||
            !symbol.HasDigits || symbol.Digits is < 0 or > ScaledValueMath.MaximumScale ||
            !symbol.HasMinVolume || !symbol.HasMaxVolume || !symbol.HasStepVolume ||
            symbol.MinVolume <= 0 || symbol.MaxVolume < symbol.MinVolume || symbol.StepVolume <= 0 ||
            !symbol.HasTradingMode || symbol.TradingMode != ProtoOATradingMode.Enabled ||
            !symbol.HasEnableShortSelling || !symbol.EnableShortSelling ||
            !symbol.HasScheduleTimeZone ||
            !string.Equals(symbol.ScheduleTimeZone, "UTC", StringComparison.OrdinalIgnoreCase) ||
            symbol.Schedule.Count == 0 ||
            symbol.Schedule.Any(static item => !item.HasStartSecond || !item.HasEndSecond))
        {
            throw new CTraderProtocolException("The symbol capability response is incomplete, disabled, or not expressed in UTC.");
        }

        var intervals = symbol.Schedule
            .Select(item => new BrokerWeeklyTradingInterval(item.StartSecond, item.EndSecond))
            .OrderBy(item => item.StartSecond)
            .ToArray();
        if (intervals.Any(static item => !item.IsValid))
            throw new CTraderProtocolException("The cTrader weekly trading schedule is invalid.");
        var closures = new List<BrokerTradingClosure>(symbol.Holiday.Count);
        foreach (var holiday in symbol.Holiday)
        {
            if (!TryMapHoliday(holiday, out var closure))
                throw new CTraderProtocolException("A cTrader holiday could not be represented as an exact UTC closure.");
            closures.Add(closure);
        }

        var digits = (byte)symbol.Digits;
        return new BrokerExecutionCapabilities(
            Version: $"ctrader-{EnvironmentLabel.ToLowerInvariant()}-openapi-{RequiredProtocolVersion}-symbol-{NativeSymbolId.Value}-d{digits}-v{symbol.MinVolume}-{symbol.MaxVolume}-{symbol.StepVolume}",
            CanonicalCapabilities: new VenueCapabilities(
                SupportedOrderTypes.Market |
                SupportedOrderTypes.Limit |
                SupportedOrderTypes.Stop,
                SupportedTimeInForce.GoodTillCancelled |
                SupportedTimeInForce.ImmediateOrCancel |
                SupportedTimeInForce.FillOrKill),
            QuantityPrecision: 2,
            MinimumQuantity: new ScaledQuantity(symbol.MinVolume, 2),
            MaximumQuantity: new ScaledQuantity(symbol.MaxVolume, 2),
            LotSize: new ScaledQuantity(symbol.StepVolume, 2),
            SupportsFractionalQuantity: false,
            PricePrecision: digits,
            TickSize: new ScaledPrice(1, digits),
            MinimumPrice: new ScaledPrice(1, digits),
            MaximumPrice: null,
            ReplaceSemantics: BrokerReplaceSemantics.InPlace,
            SupportsNativeBracket: false,
            SupportsNativeOco: false,
            TradingHours: BrokerTradingHours.FromWeeklyIntervals(intervals, closures),
            RateLimit: new BrokerRateLimit(
                _options.MaximumCommandsPerSecond,
                TimeSpan.FromSeconds(1)));
    }

    private bool TryCreateNewOrder(
        CanonicalOrderInstruction instruction,
        out ProtoOANewOrderReq? request,
        out string? reason)
    {
        request = null;
        reason = null;
        if (instruction.Terms.OrderType == CanonicalOrderType.StopLimit)
        {
            reason = "cTrader stop-limit uses a relative slippage range, not the canonical absolute limit price.";
            return false;
        }
        if (!TryToWireVolume(instruction.Terms.Quantity, out var volume) ||
            !TryMapOrderType(instruction.Terms.OrderType, out var orderType) ||
            !TryMapTimeInForce(instruction.Terms.TimeInForce, out var timeInForce))
        {
            reason = "The exact order quantity, type, or time in force is not representable by cTrader.";
            return false;
        }
        request = new ProtoOANewOrderReq
        {
            CtidTraderAccountId = NativeAccountId.Value,
            SymbolId = NativeSymbolId.Value,
            OrderType = orderType,
            TradeSide = instruction.Terms.Side == OrderSide.Buy ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell,
            Volume = volume,
            TimeInForce = timeInForce,
            ClientOrderId = instruction.Identity.ClientOrderId.Value,
        };
        if (instruction.Terms.LimitPrice is { } limit)
        {
            if (!TryToWirePrice(limit, out var value))
            {
                reason = "The exact limit price cannot round-trip through cTrader's declared decimal grid.";
                request = null;
                return false;
            }
            request.LimitPrice = value;
        }
        if (instruction.Terms.StopPrice is { } stop)
        {
            if (!TryToWirePrice(stop, out var value))
            {
                reason = "The exact stop price cannot round-trip through cTrader's declared decimal grid.";
                request = null;
                return false;
            }
            request.StopPrice = value;
        }
        return true;
    }

    private bool TryCreateAmendOrder(
        CTraderNativeOrderId orderId,
        in CanonicalOrderTerms terms,
        out ProtoOAAmendOrderReq? request,
        out string? reason)
    {
        request = null;
        reason = null;
        if (!TryToWireVolume(terms.Quantity, out var volume))
        {
            reason = "The exact replacement quantity is not representable by cTrader.";
            return false;
        }
        request = new ProtoOAAmendOrderReq
        {
            CtidTraderAccountId = NativeAccountId.Value,
            OrderId = orderId.Value,
            Volume = volume,
        };
        if (terms.LimitPrice is { } limit)
        {
            if (!TryToWirePrice(limit, out var value))
            {
                reason = "The exact replacement limit price cannot round-trip through cTrader's declared decimal grid.";
                request = null;
                return false;
            }
            request.LimitPrice = value;
        }
        if (terms.StopPrice is { } stop)
        {
            if (!TryToWirePrice(stop, out var value))
            {
                reason = "The exact replacement stop price cannot round-trip through cTrader's declared decimal grid.";
                request = null;
                return false;
            }
            request.StopPrice = value;
        }
        return true;
    }

    private void SendCommand(
        IMessage request,
        BrokerAdapterCommandKind commandKind,
        ClientOrderId clientOrderId,
        CausationId causationId)
    {
        var messageId = $"order-{Guid.NewGuid():N}";
        lock (_gate)
        {
            _pendingCommands.Add(
                messageId,
                new PendingCommandCorrelation(clientOrderId, causationId, commandKind));
        }
        try
        {
            _transport.SendAsync(CTraderOpenApiProtocol.Encode(request, messageId)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _pendingCommands.Remove(messageId);
                if (_orders.TryGetValue(clientOrderId, out var tracked))
                    _orders[clientOrderId] = tracked with { State = OrderLifecycleState.Unknown };
            }
            SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, false, false);
            throw new InvalidOperationException(
                $"The cTrader {commandKind} outcome is unknown for {clientOrderId.Value}/{causationId.Value}: {SafeReason(exception)}",
                exception);
        }
    }

    private async Task<TResponse> RequestAsync<TResponse>(
        IMessage request,
        CancellationToken cancellationToken)
        where TResponse : class, IMessage
    {
        var messageId = $"request-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_pendingRequests.TryAdd(messageId, completion))
                throw new InvalidOperationException("A cTrader request correlation collision occurred.");
        }
        try
        {
            await _transport.SendAsync(CTraderOpenApiProtocol.Encode(request, messageId), cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(_options.RequestTimeoutMilliseconds),
                cancellationToken).ConfigureAwait(false);
            if (response is ProtoOAErrorRes error)
            {
                if (IsRateLimit(error.ErrorCode))
                    ApplyRateLimitFault();
                throw new CTraderProtocolException($"cTrader error {error.ErrorCode}: {error.Description}");
            }
            if (response is not TResponse typed)
                throw new CTraderProtocolException($"Expected {typeof(TResponse).Name}, received {response.GetType().Name}.");
            return typed;
        }
        finally
        {
            lock (_gate)
                _pendingRequests.Remove(messageId);
        }
    }

    private void OnTransportMessage(ProtoMessage envelope)
    {
        object? message;
        try
        {
            message = CTraderOpenApiProtocol.TryDecodePositionUnrealizedPnlResponse(envelope, out var pnl)
                ? pnl
                : CTraderOpenApiProtocol.Decode(envelope);
        }
        catch (Exception exception)
        {
            OnTransportFault(exception);
            return;
        }
        if (message is null)
            return;

        PendingCommandCorrelation? commandCorrelation = null;
        if (envelope.HasClientMsgId)
        {
            TaskCompletionSource<object>? completion;
            lock (_gate)
            {
                _pendingRequests.TryGetValue(envelope.ClientMsgId, out completion);
                if (completion is null && _pendingCommands.Remove(envelope.ClientMsgId, out var pendingCommand))
                    commandCorrelation = pendingCommand;
            }
            if (completion is not null)
            {
                completion.TrySetResult(message);
                return;
            }
        }
        _scheduler.Schedule(() => ProcessUnsolicitedMessage(message, commandCorrelation));
    }

    private void OnTransportFault(Exception exception)
    {
        SetSession(ExecutionSessionHealth.Degraded, false, false, false);
        FailPendingRequests(exception);
        MarkPendingCommandsUnknown();
    }

    private void OnSchedulerCallbackFault(Exception exception)
    {
        _ = exception;
        RevokeExecutionCertification(isDataConnected: _transport.IsConnected);
    }

    private void ProcessUnsolicitedMessage(
        object message,
        PendingCommandCorrelation? commandCorrelation)
    {
        switch (message)
        {
            case ProtoOAExecutionEvent execution when execution.CtidTraderAccountId == NativeAccountId.Value:
                HandleExecutionEvent(execution, commandCorrelation);
                if (execution.DepositWithdraw is not null || execution.BonusDepositWithdraw is not null)
                    RefreshAndPublishCash();
                break;
            case ProtoOAOrderErrorEvent error when error.CtidTraderAccountId == NativeAccountId.Value:
                HandleOrderError(error, commandCorrelation);
                break;
            case ProtoOATraderUpdatedEvent trader when trader.CtidTraderAccountId == NativeAccountId.Value:
                if (trader.Trader is null ||
                    trader.Trader.CtidTraderAccountId != NativeAccountId.Value ||
                    !trader.Trader.HasAccessRights ||
                    trader.Trader.AccessRights != ProtoOAAccessRights.FullAccess)
                {
                    RevokeExecutionCertification(isDataConnected: true);
                }
                else
                {
                    RefreshAndPublishCash();
                }
                break;
            case ProtoOASymbolChangedEvent changed
                when changed.CtidTraderAccountId == NativeAccountId.Value &&
                     changed.SymbolId.Contains(NativeSymbolId.Value):
                RevokeExecutionCertification(isDataConnected: true);
                break;
            case ProtoOAMarginChangedEvent margin when margin.CtidTraderAccountId == NativeAccountId.Value:
                RefreshAndPublishCash();
                break;
            case ProtoOAAccountsTokenInvalidatedEvent invalidated
                when invalidated.CtidTraderAccountIds.Contains(NativeAccountId.Value):
                SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
                FailPendingRequests(new CTraderProtocolException("The cTrader access token was invalidated."));
                MarkPendingCommandsUnknown();
                break;
            case ProtoOAAccountDisconnectEvent disconnected
                when disconnected.CtidTraderAccountId == NativeAccountId.Value:
                SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
                FailPendingRequests(new CTraderProtocolException("The cTrader account session was disconnected by the server."));
                MarkPendingCommandsUnknown();
                break;
            case ProtoOAErrorRes error:
                if (commandCorrelation is { } command)
                {
                    HandleCommandError(error, command);
                }
                else if (IsRateLimit(error.ErrorCode))
                {
                    ApplyRateLimitFault();
                }
                else
                {
                    SetSession(ExecutionSessionHealth.Degraded, _transport.IsConnected, false, false);
                }
                break;
        }
    }

    private void HandleExecutionEvent(
        ProtoOAExecutionEvent execution,
        PendingCommandCorrelation? commandCorrelation)
    {
        if (execution.Order is null ||
            !TryResolveExecutionOrder(execution.Order, commandCorrelation, out var clientOrderId, out var tracked))
            return;

        var occurredAtUtc = EventTime(execution);
        var brokerOrderId = execution.Order.OrderId > 0
            ? new BrokerOrderId(execution.Order.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : tracked!.BrokerOrderId;
        if (brokerOrderId is { } rememberedBrokerId)
        {
            lock (_gate)
                _brokerToClient[rememberedBrokerId] = clientOrderId;
        }
        var causationId = tracked!.PendingCausationId;
        var kind = execution.ExecutionType switch
        {
            ProtoOAExecutionType.OrderAccepted => VenueEventKind.Acknowledged,
            ProtoOAExecutionType.OrderFilled or ProtoOAExecutionType.OrderPartialFill => VenueEventKind.Fill,
            ProtoOAExecutionType.OrderReplaced => VenueEventKind.Replaced,
            ProtoOAExecutionType.OrderCancelled => VenueEventKind.Cancelled,
            ProtoOAExecutionType.OrderExpired => VenueEventKind.Expired,
            ProtoOAExecutionType.OrderRejected => VenueEventKind.Rejected,
            ProtoOAExecutionType.OrderCancelRejected => VenueEventKind.OutcomeUnknown,
            _ => (VenueEventKind?)null,
        };
        if (!kind.HasValue)
            return;

        FillExecution? fill = null;
        CanonicalOrderTerms? replacement = null;
        var protocolFault = false;
        var reason = execution.HasErrorCode ? execution.ErrorCode : null;
        if (kind != VenueEventKind.Rejected &&
            kind != VenueEventKind.OutcomeUnknown &&
            !TryValidateObservedOrder(execution, tracked, out reason))
        {
            kind = VenueEventKind.OutcomeUnknown;
            protocolFault = true;
        }
        else if (kind == VenueEventKind.Fill)
        {
            if (!TryMapFill(execution, tracked, out fill, out reason))
            {
                kind = VenueEventKind.OutcomeUnknown;
                fill = null;
                protocolFault = true;
            }
            else if (fill is { } exactFill &&
                     !FilledQuantity(tracked.FilledQuantity, exactFill.Quantity, tracked.CurrentTerms.Quantity, out _))
            {
                kind = VenueEventKind.OutcomeUnknown;
                fill = null;
                reason = "The cTrader fill exceeded the exact remaining order quantity.";
                protocolFault = true;
            }
        }
        else if (kind == VenueEventKind.Replaced)
        {
            replacement = tracked.PendingReplacement;
            if (!replacement.HasValue && TryMapOrderTerms(execution.Order, out var mappedTerms))
            {
                replacement = mappedTerms;
            }
            else if (!replacement.HasValue)
            {
                kind = VenueEventKind.OutcomeUnknown;
                reason = "The cTrader replacement terms were not exactly representable.";
                protocolFault = true;
            }
        }

        if (protocolFault)
            RevokeExecutionCertification(isDataConnected: true);

        var dedup = ExecutionDeduplicationKey(execution, kind.Value);
        var venueEvent = new VenueEvent(
            kind.Value,
            clientOrderId,
            brokerOrderId,
            null,
            fill,
            replacement,
            occurredAtUtc,
            causationId,
            dedup,
            reason);

        UpdateTrackedOrder(clientOrderId, tracked, venueEvent);
        if (kind == VenueEventKind.Fill)
        {
            EventReceived?.Invoke(new BrokerExecutionEvent(
                new BrokerAdapterEventId($"{dedup.Value}:execution"),
                Account,
                clientOrderId,
                occurredAtUtc,
                venueEvent));
            if (fill is { } exactFill)
            {
                EventReceived?.Invoke(new BrokerCommissionEvent(
                    new BrokerAdapterEventId($"{dedup.Value}:commission"),
                    Account,
                    clientOrderId,
                    occurredAtUtc,
                    causationId,
                    exactFill.Fee));
            }
            if (execution.Position is not null && TryMapPosition(execution.Position, occurredAtUtc, out var position))
            {
                EventReceived?.Invoke(new BrokerPositionEvent(
                    new BrokerAdapterEventId($"{dedup.Value}:position"),
                    Account,
                    clientOrderId,
                    occurredAtUtc,
                    causationId,
                    Instrument,
                    position!.Quantity));
            }
        }
        else
        {
            EventReceived?.Invoke(new BrokerOrderEvent(
                new BrokerAdapterEventId($"{dedup.Value}:order"),
                Account,
                clientOrderId,
                occurredAtUtc,
                venueEvent));
        }
    }

    private void HandleOrderError(
        ProtoOAOrderErrorEvent error,
        PendingCommandCorrelation? commandCorrelation)
    {
        BrokerOrderId? brokerOrderId = error.HasOrderId && error.OrderId > 0
            ? new BrokerOrderId(error.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        ClientOrderId clientOrderId;
        TrackedOrder tracked;
        lock (_gate)
        {
            if (brokerOrderId is { } nativeOrder &&
                _brokerToClient.TryGetValue(nativeOrder, out clientOrderId) &&
                _orders.TryGetValue(clientOrderId, out tracked!))
            {
                // Resolved by the native order identity.
            }
            else if (commandCorrelation is { } command &&
                     _orders.TryGetValue(command.ClientOrderId, out tracked!))
            {
                clientOrderId = command.ClientOrderId;
            }
            else
            {
                return;
            }
        }
        if (IsRateLimit(error.ErrorCode))
            ApplyRateLimitFault();
        var kind = tracked.PendingCommand == BrokerAdapterCommandKind.Submit
            ? VenueEventKind.Rejected
            : VenueEventKind.OutcomeUnknown;
        var occurredAtUtc = UtcNow();
        var dedup = new DeduplicationKey(
            $"ctrader:{NativeAccountId.Value}:order-error:{error.OrderId}:{HashText(error.ErrorCode + error.Description + clientOrderId.Value)}");
        var venueEvent = new VenueEvent(
            kind,
            clientOrderId,
            brokerOrderId,
            null,
            null,
            null,
            occurredAtUtc,
            tracked.PendingCausationId,
            dedup,
            $"{error.ErrorCode}: {error.Description}");
        UpdateTrackedOrder(clientOrderId, tracked, venueEvent);
        EventReceived?.Invoke(new BrokerOrderEvent(
            new BrokerAdapterEventId($"{dedup.Value}:order"),
            Account,
            clientOrderId,
            occurredAtUtc,
            venueEvent));
    }

    private void HandleCommandError(
        ProtoOAErrorRes error,
        in PendingCommandCorrelation command)
    {
        TrackedOrder tracked;
        lock (_gate)
        {
            if (!_orders.TryGetValue(command.ClientOrderId, out tracked!))
                return;
        }
        if (IsRateLimit(error.ErrorCode))
            ApplyRateLimitFault();
        var kind = command.CommandKind == BrokerAdapterCommandKind.Submit
            ? VenueEventKind.Rejected
            : VenueEventKind.OutcomeUnknown;
        var occurredAtUtc = UtcNow();
        var dedup = new DeduplicationKey(
            $"ctrader:{NativeAccountId.Value}:command-error:{command.CommandKind}:{HashText(error.ErrorCode + error.Description + command.ClientOrderId.Value)}");
        var venueEvent = new VenueEvent(
            kind,
            command.ClientOrderId,
            tracked.BrokerOrderId,
            null,
            null,
            null,
            occurredAtUtc,
            command.CausationId,
            dedup,
            $"{error.ErrorCode}: {error.Description}");
        UpdateTrackedOrder(command.ClientOrderId, tracked, venueEvent);
        EventReceived?.Invoke(new BrokerOrderEvent(
            new BrokerAdapterEventId($"{dedup.Value}:order"),
            Account,
            command.ClientOrderId,
            occurredAtUtc,
            venueEvent));
    }

    private void RefreshAndPublishCash()
    {
        var refresh = RefreshReconciliationAsync().GetAwaiter().GetResult();
        if (refresh.IsSuccess)
        {
            foreach (var cash in refresh.Snapshot.Cash)
                CashSnapshotReceived?.Invoke(cash);
        }
    }

    private bool TryBuildSnapshot(
        ProtoOAReconcileRes reconcile,
        ProtoOAOrderListRes orderList,
        ProtoOATrader trader,
        ProtoOAAssetListRes assets,
        DateTime capturedAtUtc,
        out BrokerReconciliationSnapshot? snapshot,
        out string? reason)
    {
        snapshot = null;
        reason = null;
        var openOrders = new List<VenueOrderSnapshot>();
        foreach (var order in reconcile.Order)
        {
            if (order.TradeData is { } tradeData && tradeData.SymbolId != NativeSymbolId.Value)
                continue;
            if (!TryMapOrderSnapshot(order, out var mapped, out reason))
                return false;
            if (mapped is not null && !OrderLifecycle.IsTerminal(mapped.State))
                openOrders.Add(mapped);
        }

        var completedOrders = new List<VenueOrderSnapshot>();
        foreach (var order in orderList.Order)
        {
            if (order.TradeData is { } tradeData && tradeData.SymbolId != NativeSymbolId.Value)
                continue;
            if (!TryMapOrderSnapshot(order, out var mapped, out reason))
                return false;
            if (mapped is not null && OrderLifecycle.IsTerminal(mapped.State))
                completedOrders.Add(mapped);
        }

        long signedPosition = 0;
        foreach (var position in reconcile.Position)
        {
            if (position.TradeData is { } tradeData && tradeData.SymbolId != NativeSymbolId.Value)
                continue;
            if (!TryMapPosition(position, capturedAtUtc, out var mappedPosition))
            {
                reason = "A cTrader position was outside the configured symbol or whole-unit seam.";
                return false;
            }
            try
            {
                signedPosition = checked(signedPosition + mappedPosition!.Quantity.Coefficient);
            }
            catch (OverflowException)
            {
                reason = "The aggregate cTrader position exceeded the exact quantity range.";
                return false;
            }
        }
        var positions = signedPosition == 0
            ? Array.Empty<BrokerPositionSnapshot>()
            : [new BrokerPositionSnapshot(Instrument, ScaledQuantity.FromWhole(signedPosition), capturedAtUtc)];

        if (!TryMapCash(trader, assets, capturedAtUtc, out var cash, out reason))
            return false;

        openOrders.Sort(static (left, right) => string.CompareOrdinal(
            left.Instruction.Identity.ClientOrderId.Value,
            right.Instruction.Identity.ClientOrderId.Value));
        completedOrders.Sort(static (left, right) => string.CompareOrdinal(
            left.Instruction.Identity.ClientOrderId.Value,
            right.Instruction.Identity.ClientOrderId.Value));
        snapshot = new BrokerReconciliationSnapshot(
            Account,
            capturedAtUtc,
            new ReadOnlyCollection<VenueOrderSnapshot>(openOrders),
            new ReadOnlyCollection<VenueOrderSnapshot>(completedOrders),
            Array.AsReadOnly(positions),
            Array.AsReadOnly(new[] { cash! }));
        return true;
    }

    private bool TryMapOrderSnapshot(
        ProtoOAOrder order,
        out VenueOrderSnapshot? snapshot,
        out string? reason)
    {
        snapshot = null;
        reason = null;
        if (order is null || order.OrderId <= 0 || order.TradeData is null ||
            !order.HasOrderId || !order.HasOrderStatus ||
            order.TradeData.SymbolId != NativeSymbolId.Value ||
            !TryMapOrderTerms(order, out var terms) ||
            !TryFromWireVolume(order.HasExecutedVolume ? order.ExecutedVolume : 0, allowZero: true, out var filled))
        {
            reason = "A cTrader order snapshot could not be represented exactly.";
            return false;
        }

        var brokerOrderId = new BrokerOrderId(order.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var clientOrderId = order.HasClientOrderId && new ClientOrderId(order.ClientOrderId).IsValid
            ? new ClientOrderId(order.ClientOrderId)
            : new ClientOrderId($"ctrader-broker-{order.OrderId}");
        CanonicalOrderInstruction instruction;
        lock (_gate)
        {
            if (_orders.TryGetValue(clientOrderId, out var tracked))
            {
                instruction = tracked.Instruction;
            }
            else
            {
                instruction = CreateExternalInstruction(clientOrderId, brokerOrderId, terms, order);
            }
        }
        if (instruction.Validate() != OrderDomainFault.None)
        {
            reason = "A cTrader order snapshot produced an invalid canonical instruction.";
            return false;
        }

        var state = order.OrderStatus switch
        {
            ProtoOAOrderStatus.OrderStatusAccepted => filled.Coefficient > 0
                ? OrderLifecycleState.PartiallyFilled
                : OrderLifecycleState.Working,
            ProtoOAOrderStatus.OrderStatusFilled => OrderLifecycleState.Filled,
            ProtoOAOrderStatus.OrderStatusRejected => OrderLifecycleState.Rejected,
            ProtoOAOrderStatus.OrderStatusExpired => OrderLifecycleState.Expired,
            ProtoOAOrderStatus.OrderStatusCancelled => OrderLifecycleState.Cancelled,
            _ => OrderLifecycleState.Unknown,
        };
        snapshot = new VenueOrderSnapshot(instruction, terms, state, brokerOrderId, null, filled);
        lock (_gate)
        {
            _orders[clientOrderId] = new TrackedOrder(
                instruction,
                terms,
                state,
                brokerOrderId,
                filled,
                instruction.Identity.CausationId,
                BrokerAdapterCommandKind.Submit,
                null);
            _brokerToClient[brokerOrderId] = clientOrderId;
        }
        return true;
    }

    private CanonicalOrderInstruction CreateExternalInstruction(
        ClientOrderId clientOrderId,
        BrokerOrderId brokerOrderId,
        in CanonicalOrderTerms terms,
        ProtoOAOrder order)
    {
        var seed = $"ctrader-{NativeAccountId.Value}-{order.OrderId}";
        var signedUnits = terms.Side == OrderSide.Buy
            ? terms.Quantity
            : new ScaledQuantity(-terms.Quantity.Coefficient, terms.Quantity.Scale);
        var intent = new TradeIntent(
            Instrument,
            TradeIntentQuantityMode.Delta,
            signedUnits,
            null,
            null,
            ScaledMoney.Zero,
            "ctrader.external-reconciliation",
            order.TradeData?.OpenTimestamp ?? 0,
            "ctrader-openapi-2.0-reconciliation");
        var identity = new OrderIdentity(
            new IntentId($"intent-{seed}"),
            null,
            new LegId($"leg-{seed}"),
            clientOrderId,
            brokerOrderId,
            null,
            new CorrelationId($"correlation-{seed}"),
            new CausationId($"causation-{seed}"),
            new ExecutionLeaseId($"lease-{seed}"),
            new FencingToken(1));
        return new CanonicalOrderInstruction(identity, intent, terms);
    }

    private bool TryMapOrderTerms(ProtoOAOrder order, out CanonicalOrderTerms terms)
    {
        terms = default;
        if (order.TradeData is null ||
            !order.HasOrderType ||
            !order.TradeData.HasSymbolId ||
            order.TradeData.SymbolId != NativeSymbolId.Value ||
            !order.TradeData.HasTradeSide ||
            !order.TradeData.HasVolume ||
            order.TradeData.TradeSide is not (ProtoOATradeSide.Buy or ProtoOATradeSide.Sell) ||
            !order.HasTimeInForce ||
            !TryFromWireVolume(order.TradeData.Volume, allowZero: false, out var quantity) ||
            !TryMapOrderType(order.OrderType, out var orderType) ||
            !TryMapTimeInForce(order.TimeInForce, out var timeInForce))
            return false;

        ScaledPrice? limit = null;
        ScaledPrice? stop = null;
        if (orderType is CanonicalOrderType.Limit or CanonicalOrderType.StopLimit)
        {
            if (!order.HasLimitPrice || !TryFromWirePrice(order.LimitPrice, out var mapped))
                return false;
            limit = mapped;
        }
        if (orderType is CanonicalOrderType.Stop or CanonicalOrderType.StopLimit)
        {
            if (!order.HasStopPrice || !TryFromWirePrice(order.StopPrice, out var mapped))
                return false;
            stop = mapped;
        }
        terms = new CanonicalOrderTerms(
            order.TradeData.TradeSide == ProtoOATradeSide.Buy ? OrderSide.Buy : OrderSide.Sell,
            orderType,
            timeInForce,
            quantity,
            limit,
            stop);
        return terms.Validate() == OrderDomainFault.None;
    }

    private static bool TryMapHoliday(
        ProtoOAHoliday holiday,
        out BrokerTradingClosure closure)
    {
        closure = default;
        if (!holiday.HasHolidayDate ||
            !holiday.HasIsRecurring ||
            !holiday.HasScheduleTimeZone ||
            !string.Equals(holiday.ScheduleTimeZone, "UTC", StringComparison.OrdinalIgnoreCase) ||
            holiday.HasStartSecond != holiday.HasEndSecond)
        {
            return false;
        }
        var start = holiday.HasStartSecond ? holiday.StartSecond : 0;
        var end = holiday.HasEndSecond ? holiday.EndSecond : BrokerWeeklyTradingInterval.SecondsPerDay;
        if (start < 0 || end <= start || end > BrokerWeeklyTradingInterval.SecondsPerDay)
            return false;
        try
        {
            var date = DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(holiday.HolidayDate));
            closure = new BrokerTradingClosure(date, holiday.IsRecurring, (uint)start, (uint)end);
            return closure.IsValid;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private bool TryValidateObservedOrder(
        ProtoOAExecutionEvent execution,
        TrackedOrder tracked,
        out string? reason)
    {
        reason = null;
        var order = execution.Order;
        if (!execution.HasExecutionType ||
            order is null ||
            !order.HasOrderId ||
            order.OrderId <= 0 ||
            !order.HasOrderStatus ||
            !TryMapOrderTerms(order, out var observedTerms))
        {
            reason = "The cTrader execution event omitted an exactly representable order.";
            return false;
        }
        var expectedTerms = execution.ExecutionType == ProtoOAExecutionType.OrderReplaced &&
                            tracked.PendingReplacement is { } pendingReplacement
            ? pendingReplacement
            : tracked.CurrentTerms;
        if (observedTerms != expectedTerms)
        {
            reason = "The cTrader execution event changed order terms without an exact requested replacement.";
            return false;
        }
        var statusMatches = execution.ExecutionType switch
        {
            ProtoOAExecutionType.OrderAccepted => order.OrderStatus == ProtoOAOrderStatus.OrderStatusAccepted,
            ProtoOAExecutionType.OrderPartialFill => order.OrderStatus == ProtoOAOrderStatus.OrderStatusAccepted,
            ProtoOAExecutionType.OrderFilled => order.OrderStatus == ProtoOAOrderStatus.OrderStatusFilled,
            ProtoOAExecutionType.OrderReplaced => order.OrderStatus == ProtoOAOrderStatus.OrderStatusAccepted,
            ProtoOAExecutionType.OrderCancelled => order.OrderStatus == ProtoOAOrderStatus.OrderStatusCancelled,
            ProtoOAExecutionType.OrderExpired => order.OrderStatus == ProtoOAOrderStatus.OrderStatusExpired,
            _ => true,
        };
        if (!statusMatches)
        {
            reason = "The cTrader execution type and order status were inconsistent.";
            return false;
        }
        return true;
    }

    private bool TryMapFill(
        ProtoOAExecutionEvent execution,
        TrackedOrder tracked,
        out FillExecution? fill,
        out string? reason)
    {
        fill = null;
        reason = null;
        var deal = execution.Deal;
        if (deal is null)
        {
            reason = "The cTrader fill event did not contain a deal.";
            return false;
        }
        if (execution.Order is null ||
            !deal.HasOrderId ||
            !deal.HasSymbolId ||
            !deal.HasTradeSide ||
            !deal.HasDealStatus ||
            deal.OrderId != execution.Order.OrderId ||
            deal.SymbolId != NativeSymbolId.Value ||
            deal.TradeSide is not (ProtoOATradeSide.Buy or ProtoOATradeSide.Sell) ||
            deal.TradeSide != execution.Order.TradeData?.TradeSide ||
            execution.ExecutionType == ProtoOAExecutionType.OrderFilled && deal.DealStatus != ProtoOADealStatus.Filled ||
            execution.ExecutionType == ProtoOAExecutionType.OrderPartialFill && deal.DealStatus != ProtoOADealStatus.PartiallyFilled)
        {
            reason = "The cTrader deal identity, side, symbol, or status did not match the order event.";
            return false;
        }
        if (!deal.HasFilledVolume || deal.FilledVolume <= 0 ||
            !TryFromWireVolume(deal.FilledVolume, allowZero: false, out var quantity) ||
            !deal.HasExecutionPrice || !TryFromWirePrice(deal.ExecutionPrice, out var price))
        {
            reason = "The cTrader fill quantity or price was not exactly representable.";
            return false;
        }
        var moneyDigits = deal.HasMoneyDigits ? deal.MoneyDigits : 0;
        if (deal.HasCommission && deal.Commission != 0 && !deal.HasMoneyDigits)
        {
            reason = "The cTrader deal omitted money digits for a non-zero commission.";
            return false;
        }
        if (moneyDigits > ScaledValueMath.MaximumScale || !TryFeeMagnitude(deal.HasCommission ? deal.Commission : 0, out var commission))
        {
            reason = "The cTrader commission was outside the exact money range.";
            return false;
        }
        var fee = new ScaledMoney(commission, (byte)moneyDigits);
        fill = new FillExecution(quantity, price, fee, LiquidityFlag.Taker);
        reason = "cTrader does not report maker/taker; the canonical seam has no unknown value, so non-economic liquidity metadata is conservatively classified as taker.";
        return fill.Value.IsValid;
    }

    private bool TryMapPosition(
        ProtoOAPosition position,
        DateTime observedAtUtc,
        out BrokerPositionSnapshot? snapshot)
    {
        snapshot = null;
        if (position.TradeData is null ||
            !position.TradeData.HasSymbolId ||
            position.TradeData.SymbolId != NativeSymbolId.Value ||
            !position.TradeData.HasTradeSide ||
            !position.TradeData.HasVolume ||
            position.TradeData.TradeSide is not (ProtoOATradeSide.Buy or ProtoOATradeSide.Sell) ||
            !TryFromWireVolume(position.TradeData.Volume, allowZero: false, out var quantity))
            return false;
        if (!quantity.TryGetWholeUnits(out var units))
            return false;
        var signed = position.TradeData.TradeSide == ProtoOATradeSide.Buy ? units : -units;
        snapshot = new BrokerPositionSnapshot(Instrument, ScaledQuantity.FromWhole(signed), observedAtUtc);
        return true;
    }

    private static bool TryMapCash(
        ProtoOATrader trader,
        ProtoOAAssetListRes assets,
        DateTime observedAtUtc,
        out BrokerCashSnapshot? snapshot,
        out string? reason)
    {
        snapshot = null;
        reason = null;
        if (!trader.HasBalance || !trader.HasMoneyDigits || trader.MoneyDigits > ScaledValueMath.MaximumScale ||
            !trader.HasDepositAssetId)
        {
            reason = "The cTrader trader response omitted exact balance metadata.";
            return false;
        }
        var asset = assets.Asset.FirstOrDefault(item => item.AssetId == trader.DepositAssetId);
        var currency = asset is { HasName: true } ? asset.Name : asset is { HasDisplayName: true } ? asset.DisplayName : null;
        if (string.IsNullOrWhiteSpace(currency))
        {
            reason = "The cTrader deposit currency could not be resolved.";
            return false;
        }

        // ProtoOATrader exposes exact ledger cash balance, not equity/free margin. Keep both cash
        // fields on that exact basis; never synthesize buying power from margin without unrealized PnL.
        var balance = new ScaledMoney(trader.Balance, (byte)trader.MoneyDigits);
        snapshot = new BrokerCashSnapshot(
            currency,
            balance,
            balance,
            observedAtUtc);
        return true;
    }

    private void UpdateTrackedOrder(ClientOrderId clientOrderId, TrackedOrder tracked, VenueEvent venueEvent)
    {
        var state = venueEvent.Kind switch
        {
            VenueEventKind.Acknowledged => OrderLifecycleState.Working,
            VenueEventKind.Fill when venueEvent.Fill is { } fill =>
                FilledQuantity(tracked.FilledQuantity, fill.Quantity, tracked.CurrentTerms.Quantity, out var total)
                    ? total == tracked.CurrentTerms.Quantity
                        ? OrderLifecycleState.Filled
                        : OrderLifecycleState.PartiallyFilled
                    : OrderLifecycleState.Unknown,
            VenueEventKind.Cancelled => OrderLifecycleState.Cancelled,
            VenueEventKind.Replaced => OrderLifecycleState.Working,
            VenueEventKind.Rejected => OrderLifecycleState.Rejected,
            VenueEventKind.Expired => OrderLifecycleState.Expired,
            VenueEventKind.OutcomeUnknown => OrderLifecycleState.Unknown,
            _ => tracked.State,
        };
        var filledQuantity = tracked.FilledQuantity;
        if (venueEvent.Kind == VenueEventKind.Fill && venueEvent.Fill is { } exactFill &&
            FilledQuantity(tracked.FilledQuantity, exactFill.Quantity, tracked.CurrentTerms.Quantity, out var totalFilled))
            filledQuantity = totalFilled;
        lock (_gate)
        {
            _orders[clientOrderId] = tracked with
            {
                CurrentTerms = venueEvent.ReplacementTerms ?? tracked.CurrentTerms,
                State = state,
                BrokerOrderId = venueEvent.BrokerOrderId ?? tracked.BrokerOrderId,
                FilledQuantity = filledQuantity,
                PendingReplacement = null,
            };
        }
    }

    private static bool FilledQuantity(
        ScaledQuantity current,
        ScaledQuantity delta,
        ScaledQuantity maximum,
        out ScaledQuantity total)
    {
        total = default;
        if (!current.TryGetWholeUnits(out var currentUnits) ||
            !delta.TryGetWholeUnits(out var deltaUnits) ||
            !maximum.TryGetWholeUnits(out var maximumUnits))
            return false;
        try
        {
            var sum = checked(currentUnits + deltaUnits);
            if (sum < 0 || sum > maximumUnits)
                return false;
            total = ScaledQuantity.FromWhole(sum);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private bool TryResolveExecutionOrder(
        ProtoOAOrder order,
        PendingCommandCorrelation? commandCorrelation,
        out ClientOrderId clientOrderId,
        out TrackedOrder? tracked)
    {
        if (order.HasClientOrderId)
        {
            var candidate = new ClientOrderId(order.ClientOrderId);
            lock (_gate)
            {
                if (candidate.IsValid && _orders.TryGetValue(candidate, out tracked))
                {
                    clientOrderId = candidate;
                    return true;
                }
            }
        }
        if (order.OrderId > 0)
        {
            var brokerOrderId = new BrokerOrderId(order.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            lock (_gate)
            {
                if (_brokerToClient.TryGetValue(brokerOrderId, out clientOrderId) &&
                    _orders.TryGetValue(clientOrderId, out tracked))
                    return true;
            }
        }
        if (commandCorrelation is { } command)
        {
            lock (_gate)
            {
                if (_orders.TryGetValue(command.ClientOrderId, out tracked))
                {
                    clientOrderId = command.ClientOrderId;
                    return true;
                }
            }
        }
        clientOrderId = default;
        tracked = null;
        return false;
    }

    private bool TryResolve(
        in BrokerOrderQuery query,
        out ClientOrderId clientOrderId,
        out TrackedOrder? tracked)
    {
        lock (_gate)
        {
            if (query.ClientOrderId is { } candidate && _orders.TryGetValue(candidate, out tracked))
            {
                clientOrderId = candidate;
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

    private static VenueOrderSnapshot ToSnapshot(TrackedOrder tracked) =>
        new(
            tracked.Instruction,
            tracked.CurrentTerms,
            tracked.State,
            tracked.BrokerOrderId,
            null,
            tracked.FilledQuantity);

    private bool TryConsumeRateBudget()
    {
        lock (_gate)
        {
            var now = UtcNow();
            var rateLimit = _capabilities.RateLimit;
            if (now < _rateWindowStartedUtc || now - _rateWindowStartedUtc >= rateLimit.Window)
            {
                _rateWindowStartedUtc = now;
                _commandsInRateWindow = 0;
            }
            if (_commandsInRateWindow >= rateLimit.MaximumCommands)
                return false;
            _commandsInRateWindow++;
            return true;
        }
    }

    private bool TryToWireVolume(ScaledQuantity quantity, out long wireVolume)
    {
        wireVolume = 0;
        if (!quantity.TryGetWholeUnits(out var units) || units <= 0)
            return false;
        try
        {
            wireVolume = checked(units * 100);
            return wireVolume > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryFromWireVolume(long wireVolume, bool allowZero, out ScaledQuantity quantity)
    {
        quantity = ScaledQuantity.Zero;
        if (wireVolume < 0 || !allowZero && wireVolume == 0)
            return false;
        var scaled = new ScaledQuantity(wireVolume, 2);
        if (!scaled.TryGetWholeUnits(out var units))
            return false;
        quantity = ScaledQuantity.FromWhole(units);
        return allowZero || units > 0;
    }

    private bool TryToWirePrice(ScaledPrice price, out double value)
    {
        value = 0;
        if (!price.IsValid || price.Coefficient <= 0 || price.Scale > _priceDigits)
            return false;
        try
        {
            value = (double)((decimal)price.Coefficient / (decimal)ScaledValueMath.Pow10(price.Scale));
        }
        catch (OverflowException)
        {
            return false;
        }
        if (!ScaledValueMath.TryQuantizeDouble(value, _priceDigits, out var roundTrip) ||
            !ScaledValueMath.TryAlign(
                price.Coefficient,
                price.Scale,
                roundTrip,
                _priceDigits,
                out var expected,
                out var actual,
                out _) ||
            expected != actual)
            return false;
        return true;
    }

    private bool TryFromWirePrice(double value, out ScaledPrice price)
    {
        price = default;
        if (!ScaledValueMath.TryQuantizeDouble(value, _priceDigits, out var coefficient) || coefficient <= 0)
            return false;
        price = new ScaledPrice(coefficient, _priceDigits);
        return true;
    }

    private static bool TryFeeMagnitude(long commission, out long magnitude)
    {
        if (commission == long.MinValue)
        {
            magnitude = 0;
            return false;
        }
        magnitude = Math.Abs(commission);
        return true;
    }

    private static bool TryMapOrderType(CanonicalOrderType source, out ProtoOAOrderType target)
    {
        target = source switch
        {
            CanonicalOrderType.Market => ProtoOAOrderType.Market,
            CanonicalOrderType.Limit => ProtoOAOrderType.Limit,
            CanonicalOrderType.Stop => ProtoOAOrderType.Stop,
            _ => default,
        };
        return source is CanonicalOrderType.Market or CanonicalOrderType.Limit or CanonicalOrderType.Stop;
    }

    private static bool TryMapOrderType(ProtoOAOrderType source, out CanonicalOrderType target)
    {
        target = source switch
        {
            ProtoOAOrderType.Market => CanonicalOrderType.Market,
            ProtoOAOrderType.Limit => CanonicalOrderType.Limit,
            ProtoOAOrderType.Stop => CanonicalOrderType.Stop,
            _ => default,
        };
        return source is ProtoOAOrderType.Market or ProtoOAOrderType.Limit or ProtoOAOrderType.Stop;
    }

    private static bool TryMapTimeInForce(CanonicalTimeInForce source, out ProtoOATimeInForce target)
    {
        target = source switch
        {
            CanonicalTimeInForce.GoodTillCancelled => ProtoOATimeInForce.GoodTillCancel,
            CanonicalTimeInForce.ImmediateOrCancel => ProtoOATimeInForce.ImmediateOrCancel,
            CanonicalTimeInForce.FillOrKill => ProtoOATimeInForce.FillOrKill,
            _ => default,
        };
        return source is CanonicalTimeInForce.GoodTillCancelled or CanonicalTimeInForce.ImmediateOrCancel or CanonicalTimeInForce.FillOrKill;
    }

    private static bool TryMapTimeInForce(ProtoOATimeInForce source, out CanonicalTimeInForce target)
    {
        target = source switch
        {
            ProtoOATimeInForce.GoodTillCancel => CanonicalTimeInForce.GoodTillCancelled,
            ProtoOATimeInForce.ImmediateOrCancel => CanonicalTimeInForce.ImmediateOrCancel,
            ProtoOATimeInForce.FillOrKill => CanonicalTimeInForce.FillOrKill,
            _ => default,
        };
        return source is ProtoOATimeInForce.GoodTillCancel or ProtoOATimeInForce.ImmediateOrCancel or ProtoOATimeInForce.FillOrKill;
    }

    private BrokerDispatchReceipt CreateReceipt(
        BrokerAdapterCommandKind commandKind,
        ClientOrderId clientOrderId,
        CausationId causationId)
    {
        var material = $"{Account.AdapterId.Value}|{Account.AccountId.Value}|{commandKind}|{clientOrderId.Value}|{causationId.Value}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new BrokerDispatchReceipt(
            new DispatchReceiptId($"ctrader-dispatch-{Convert.ToHexString(hash).ToLowerInvariant()}"),
            Account,
            commandKind,
            clientOrderId,
            causationId,
            UtcNow());
    }

    private DeduplicationKey ExecutionDeduplicationKey(ProtoOAExecutionEvent execution, VenueEventKind kind)
    {
        var orderId = execution.Order?.OrderId ?? 0;
        var dealId = execution.Deal?.DealId ?? 0;
        var timestamp = execution.Deal?.ExecutionTimestamp ?? execution.Order?.UtcLastUpdateTimestamp ?? 0;
        return new DeduplicationKey(
            $"ctrader:{NativeAccountId.Value}:{(int)execution.ExecutionType}:{(int)kind}:{orderId}:{dealId}:{timestamp}");
    }

    private DateTime EventTime(ProtoOAExecutionEvent execution)
    {
        var timestamp = execution.Deal?.ExecutionTimestamp ?? execution.Order?.UtcLastUpdateTimestamp ?? 0;
        try
        {
            return timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime
                : UtcNow();
        }
        catch (ArgumentOutOfRangeException)
        {
            return UtcNow();
        }
    }

    private static bool TryParseNativeOrderId(BrokerOrderId brokerOrderId, out CTraderNativeOrderId native)
    {
        native = long.TryParse(
            brokerOrderId.Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? new CTraderNativeOrderId(value)
            : default;
        return native.IsValid;
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

    private async Task<CTraderConnectionResult> DisconnectFailureAsync(
        CTraderConnectionFault fault,
        string reason)
    {
        await SafeDisconnectAsync().ConfigureAwait(false);
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        return new CTraderConnectionResult(fault, Session, reason);
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            await _transport.DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private CTraderConnectionResult ConnectionFailure(CTraderConnectionFault fault, string reason) =>
        new(fault, Session, reason);

    private bool TryValidateLiveAuthorization(out string? reason)
    {
        reason = null;
        if (Mode == ExecutionMode.Paper)
            return true;
        try
        {
            var authorizedEndpoint = CTraderExecutionEndpointGate.Resolve(_options, _liveConfirmationStore);
            if (authorizedEndpoint != _endpoint || !authorizedEndpoint.IsLive)
                throw new InvalidOperationException("The cTrader LIVE endpoint no longer matches its gated endpoint token.");
            return true;
        }
        catch (Exception exception)
        {
            reason = $"cTrader LIVE authorization is absent or was revoked: {SafeReason(exception)}";
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
        SetSession(ExecutionSessionHealth.Disconnected, false, false, false);
        MarkPendingCommandsUnknown();
    }

    private CTraderSnapshotRefreshResult SnapshotFailure(CTraderSnapshotFault fault, string reason) =>
        new(fault, CaptureReconciliationSnapshot(), reason);

    private void FailPendingRequests(Exception exception)
    {
        TaskCompletionSource<object>[] pending;
        lock (_gate)
            pending = _pendingRequests.Values.ToArray();
        foreach (var completion in pending)
            completion.TrySetException(exception);
    }

    private void MarkPendingCommandsUnknown()
    {
        lock (_gate)
        {
            foreach (var pending in _pendingCommands.Values)
            {
                if (_orders.TryGetValue(pending.ClientOrderId, out var tracked))
                    _orders[pending.ClientOrderId] = tracked with { State = OrderLifecycleState.Unknown };
            }
            _pendingCommands.Clear();
        }
    }

    private void ApplyRateLimitFault()
    {
        bool isDataConnected;
        bool isExecutionAuthenticated;
        lock (_gate)
        {
            _commandsInRateWindow = _capabilities.RateLimit.MaximumCommands;
            isDataConnected = _session.IsDataConnected && _transport.IsConnected;
            isExecutionAuthenticated = _session.IsExecutionAuthenticated;
        }
        SetSession(
            ExecutionSessionHealth.Degraded,
            isDataConnected,
            isExecutionAuthenticated,
            isExecutionCertified: false);
    }

    private void RevokeExecutionCertification(bool isDataConnected)
    {
        bool isExecutionAuthenticated;
        lock (_gate)
            isExecutionAuthenticated = _session.IsExecutionAuthenticated;
        SetSession(
            ExecutionSessionHealth.Degraded,
            isDataConnected && _transport.IsConnected,
            isExecutionAuthenticated,
            isExecutionCertified: false);
        MarkPendingCommandsUnknown();
    }

    private BrokerExecutionCapabilities UnavailableCapabilities() =>
        new(
            Version: $"ctrader-{EnvironmentLabel.ToLowerInvariant()}-unavailable",
            CanonicalCapabilities: new VenueCapabilities(SupportedOrderTypes.None, SupportedTimeInForce.None),
            QuantityPrecision: 0,
            MinimumQuantity: ScaledQuantity.FromWhole(1),
            MaximumQuantity: ScaledQuantity.FromWhole(1),
            LotSize: ScaledQuantity.FromWhole(1),
            SupportsFractionalQuantity: false,
            PricePrecision: 0,
            TickSize: new ScaledPrice(1, 0),
            MinimumPrice: new ScaledPrice(1, 0),
            MaximumPrice: null,
            ReplaceSemantics: BrokerReplaceSemantics.Unsupported,
            SupportsNativeBracket: false,
            SupportsNativeOco: false,
            TradingHours: BrokerTradingHours.AlwaysOpen,
            RateLimit: new BrokerRateLimit(1, TimeSpan.FromSeconds(1)));

    private BrokerReconciliationSnapshot EmptySnapshot(DateTime capturedAtUtc) =>
        new(
            Account,
            capturedAtUtc,
            Array.Empty<VenueOrderSnapshot>(),
            Array.Empty<VenueOrderSnapshot>(),
            Array.Empty<BrokerPositionSnapshot>(),
            Array.Empty<BrokerCashSnapshot>());

    private static BrokerReconciliationSnapshot CopySnapshot(BrokerReconciliationSnapshot snapshot) =>
        new(
            snapshot.Account,
            snapshot.CapturedAtUtc,
            Array.AsReadOnly(snapshot.OpenOrders.ToArray()),
            Array.AsReadOnly(snapshot.CompletedOrders.ToArray()),
            Array.AsReadOnly(snapshot.Positions.ToArray()),
            Array.AsReadOnly(snapshot.Cash.ToArray()));

    private DateTime UtcNow()
    {
        var value = _clock.UtcNow;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static string SafeReason(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant()[..16];

    private static bool IsRateLimit(string? errorCode) =>
        errorCode?.Contains("FREQUENCY", StringComparison.OrdinalIgnoreCase) == true ||
        errorCode?.Contains("RATE", StringComparison.OrdinalIgnoreCase) == true;

    private static BrokerAdapterCommandResult Dispatched(BrokerDispatchReceipt receipt) =>
        new(BrokerAdapterCommandStatus.Dispatched, BrokerAdapterCommandFault.None, receipt, 0, null);

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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record TrackedOrder(
        CanonicalOrderInstruction Instruction,
        CanonicalOrderTerms CurrentTerms,
        OrderLifecycleState State,
        BrokerOrderId? BrokerOrderId,
        ScaledQuantity FilledQuantity,
        CausationId PendingCausationId,
        BrokerAdapterCommandKind PendingCommand,
        CanonicalOrderTerms? PendingReplacement);

    private readonly record struct PendingCommandCorrelation(
        ClientOrderId ClientOrderId,
        CausationId CausationId,
        BrokerAdapterCommandKind CommandKind);

    private sealed class CTraderProtocolException(string message) : Exception(message);
}
