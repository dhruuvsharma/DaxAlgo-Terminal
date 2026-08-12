using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>Interactive Brokers order types supported by the execution adapter.</summary>
public enum InteractiveBrokersNativeOrderType : byte
{
    /// <summary>The native order type is not recognized by this adapter.</summary>
    Unknown = 0,

    /// <summary>Market order (<c>MKT</c>).</summary>
    Market = 1,

    /// <summary>Limit order (<c>LMT</c>).</summary>
    Limit = 2,

    /// <summary>Stop order (<c>STP</c>).</summary>
    Stop = 3,

    /// <summary>Stop-limit order (<c>STP LMT</c>).</summary>
    StopLimit = 4,

    /// <summary>Trailing-stop order (<c>TRAIL</c>).</summary>
    TrailingStop = 5,
}

/// <summary>Interactive Brokers time-in-force values supported by the execution adapter.</summary>
public enum InteractiveBrokersNativeTimeInForce : byte
{
    /// <summary>The native time-in-force is not recognized by this adapter.</summary>
    Unknown = 0,

    /// <summary>Day order (<c>DAY</c>).</summary>
    Day = 1,

    /// <summary>Good-till-cancelled order (<c>GTC</c>).</summary>
    GoodTillCancelled = 2,

    /// <summary>Immediate-or-cancel order (<c>IOC</c>).</summary>
    ImmediateOrCancel = 3,

    /// <summary>Fill-or-kill order (<c>FOK</c>).</summary>
    FillOrKill = 4,

    /// <summary>Market-on-open order (<c>OPG</c>).</summary>
    MarketOnOpen = 5,
}

/// <summary>Normalized IB order status while retaining the native status text in each snapshot.</summary>
public enum InteractiveBrokersNativeOrderStatus : byte
{
    /// <summary>The native status is not recognized.</summary>
    Unknown = 0,

    /// <summary>The order is pending submission.</summary>
    PendingSubmit = 1,

    /// <summary>The order is held before submission.</summary>
    PreSubmitted = 2,

    /// <summary>The order is working at the venue.</summary>
    Submitted = 3,

    /// <summary>Cancellation is pending.</summary>
    PendingCancel = 4,

    /// <summary>The order was cancelled.</summary>
    Cancelled = 5,

    /// <summary>The order was completely filled.</summary>
    Filled = 6,

    /// <summary>The order became inactive.</summary>
    Inactive = 7,

    /// <summary>The API cancelled the order.</summary>
    ApiCancelled = 8,

    /// <summary>The order was rejected.</summary>
    Rejected = 9,
}

/// <summary>One exact IB contract identity used by all transport operations and callbacks.</summary>
public sealed record InteractiveBrokersContract(
    int ContractId,
    string Symbol,
    string SecurityType,
    string Exchange,
    string PrimaryExchange,
    string Currency);

/// <summary>The authenticated session identity returned after the native handshake.</summary>
public sealed record InteractiveBrokersSessionSnapshot(
    string AccountId,
    int NextValidOrderId,
    DateTime ObservedAtUtc,
    bool IsPaper);

/// <summary>Native capabilities discovered for one exact IB contract.</summary>
public sealed record InteractiveBrokersNativeCapabilities(
    IReadOnlyList<InteractiveBrokersNativeOrderType> OrderTypes,
    IReadOnlyList<InteractiveBrokersNativeTimeInForce> TimeInForce,
    IReadOnlyList<string> AssetClasses,
    string SelectedAssetClass,
    ScaledQuantity MinimumOrderQuantity,
    ScaledQuantity QuantityIncrement,
    ScaledPrice MinimumPriceIncrement,
    bool SupportsOutsideRegularTradingHours,
    BrokerTradingHours TradingHoursSchedule,
    BrokerTradingHours RegularTradingHours,
    string TradingHours,
    string LiquidHours,
    DateTime ObservedAtUtc);

/// <summary>Exact native order payload used for both placement and modification.</summary>
public sealed record InteractiveBrokersOrderRequest(
    int OrderId,
    string ClientOrderId,
    string AccountId,
    InteractiveBrokersContract Contract,
    string Side,
    InteractiveBrokersNativeOrderType OrderType,
    InteractiveBrokersNativeTimeInForce TimeInForce,
    ScaledQuantity Quantity,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice,
    ScaledPrice? TrailStopPrice,
    ScaledRatio? TrailingPercent,
    bool OutsideRegularTradingHours);

/// <summary>One open, completed, cancelled, filled, or rejected IB order state.</summary>
public sealed record InteractiveBrokersOrderSnapshot(
    int OrderId,
    long PermanentId,
    string ClientOrderId,
    string AccountId,
    InteractiveBrokersContract Contract,
    string Side,
    InteractiveBrokersNativeOrderType OrderType,
    InteractiveBrokersNativeTimeInForce TimeInForce,
    InteractiveBrokersNativeOrderStatus Status,
    string NativeStatus,
    ScaledQuantity Quantity,
    ScaledQuantity FilledQuantity,
    ScaledQuantity RemainingQuantity,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice,
    ScaledPrice? TrailStopPrice,
    ScaledRatio? TrailingPercent,
    bool OutsideRegularTradingHours,
    string? WhyHeld,
    int? RejectionCode,
    string? RejectionReason,
    DateTime UpdatedAtUtc);

/// <summary>One exact fill reported by the IB <c>execDetails</c> callback.</summary>
public sealed record InteractiveBrokersExecutionSnapshot(
    string ExecutionId,
    int OrderId,
    long PermanentId,
    string ClientOrderId,
    string AccountId,
    InteractiveBrokersContract Contract,
    string Side,
    ScaledQuantity Quantity,
    ScaledPrice Price,
    ScaledQuantity CumulativeQuantity,
    ScaledPrice AveragePrice,
    string NativeExecutionTime,
    DateTime ObservedAtUtc);

/// <summary>Commission and fees associated with one execution.</summary>
public sealed record InteractiveBrokersCommissionSnapshot(
    string ExecutionId,
    ScaledMoney CommissionAndFees,
    string Currency,
    ScaledMoney? RealizedProfitAndLoss,
    DateTime ObservedAtUtc);

/// <summary>One account position from the native position stream.</summary>
public sealed record InteractiveBrokersPositionSnapshot(
    string AccountId,
    InteractiveBrokersContract Contract,
    ScaledQuantity Quantity,
    ScaledPrice AverageCost,
    DateTime ObservedAtUtc);

/// <summary>One currency-specific cash snapshot from IB account summary.</summary>
public sealed record InteractiveBrokersCashSnapshot(
    string AccountId,
    string Currency,
    ScaledMoney TotalCash,
    ScaledMoney AvailableFunds,
    DateTime ObservedAtUtc);

/// <summary>Bounded reconciliation state assembled from native snapshot callbacks.</summary>
public sealed record InteractiveBrokersReconciliationSnapshot(
    string AccountId,
    IReadOnlyList<InteractiveBrokersOrderSnapshot> OpenOrders,
    IReadOnlyList<InteractiveBrokersOrderSnapshot> CompletedOrders,
    IReadOnlyList<InteractiveBrokersPositionSnapshot> Positions,
    IReadOnlyList<InteractiveBrokersCashSnapshot> Cash,
    DateTime CapturedAtUtc);

/// <summary>One native order-scoped or request-scoped IB error.</summary>
public sealed record InteractiveBrokersOrderError(
    int? OrderId,
    string? ClientOrderId,
    int ErrorCode,
    string Message,
    string? AdvancedOrderRejectJson,
    DateTime ObservedAtUtc);

/// <summary>
/// SDK-free transport seam for the IB socket API. Tests provide a deterministic in-process peer;
/// only the guarded real implementation references <c>CSharpAPI.dll</c>.
/// </summary>
public interface IInteractiveBrokersExecutionTransport : IDisposable, IAsyncDisposable
{
    /// <summary>The already-authorized endpoint represented by this transport.</summary>
    InteractiveBrokersExecutionEndpoint Endpoint { get; }

    /// <summary>Gets whether the native socket session is open.</summary>
    bool IsConnected { get; }

    /// <summary>Raised for an order-state callback.</summary>
    event Action<InteractiveBrokersOrderSnapshot>? OrderUpdated;

    /// <summary>Raised for an execution callback.</summary>
    event Action<InteractiveBrokersExecutionSnapshot>? ExecutionReceived;

    /// <summary>Raised for a commission-and-fees callback.</summary>
    event Action<InteractiveBrokersCommissionSnapshot>? CommissionReceived;

    /// <summary>Raised for a position callback.</summary>
    event Action<InteractiveBrokersPositionSnapshot>? PositionUpdated;

    /// <summary>Raised for a native order or request rejection.</summary>
    event Action<InteractiveBrokersOrderError>? OrderError;

    /// <summary>Raised when the socket or reader becomes unusable.</summary>
    event Action<Exception>? Faulted;

    /// <summary>Connects and authenticates one expected account identity.</summary>
    Task<InteractiveBrokersSessionSnapshot> ConnectAsync(
        int clientId,
        string expectedAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects the socket and reader.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Discovers exact contract capabilities from IB contract details.</summary>
    Task<InteractiveBrokersNativeCapabilities> DiscoverCapabilitiesAsync(
        InteractiveBrokersContract contract,
        CancellationToken cancellationToken = default);

    /// <summary>Reserves the next native integer order identifier.</summary>
    int ReserveOrderId();

    /// <summary>Places a new order.</summary>
    Task PlaceOrderAsync(
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the exact native/client order identity.</summary>
    Task CancelOrderAsync(
        int orderId,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing order by resubmitting the same native order identifier.</summary>
    Task ModifyOrderAsync(
        InteractiveBrokersOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Obtains open, completed, position, and cash snapshots for reconciliation.</summary>
    Task<InteractiveBrokersReconciliationSnapshot> GetReconciliationSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates the guarded native transport without exposing conditional SDK types to callers.</summary>
public static class InteractiveBrokersExecutionTransportFactory
{
    /// <summary>Default bound on native order identities and reconciliation rows.</summary>
    public const int DefaultMaximumTrackedOrders = 4_096;

    /// <summary>
    /// Creates the real TWS transport when <c>HAS_IBAPI</c> is available; otherwise fails clearly
    /// when an explicitly enabled adapter is resolved.
    /// </summary>
    public static IInteractiveBrokersExecutionTransport CreateDefault(
        InteractiveBrokersExecutionEndpoint endpoint,
        TimeSpan timeout,
        int maximumTrackedOrders = DefaultMaximumTrackedOrders)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumTrackedOrders is < 32 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedOrders));

#if HAS_IBAPI
        return new InteractiveBrokersTwsExecutionTransport(endpoint, timeout, maximumTrackedOrders);
#else
        throw new InvalidOperationException(
            "Interactive Brokers execution is enabled, but CSharpAPI.dll was not resolved at build time. " +
            "Install the TWS API or set TwsApiClientDll, then rebuild the Execution project.");
#endif
    }
}
