using System.Reactive.Subjects;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Sandbox;

/// <summary>
/// Adapts legacy imperative market orders into signed targets on one host-owned virtual book.
/// Accepted orders fill immediately at the latest valid reference price; quantities map 1:1 from
/// legacy integer units to virtual-book reference units.
/// </summary>
/// <remarks>
/// <para>
/// The virtual book cannot preserve pending order lifetimes. Under this compatibility model every
/// defined legacy order type is therefore reconciled immediately at the reference price, while
/// <see cref="OrderRequest.StopPrice"/> and <see cref="OrderRequest.LimitPrice"/> are projected as
/// the virtual protective stop and profit target. Those fields are conditional-entry prices in the
/// legacy contract, so this projection is an explicit compatibility approximation rather than an
/// execution-fidelity claim.
/// </para>
/// <para>
/// Cancellation removes the accepted order's contribution from the net virtual target. The legacy
/// router contract exposes no replace operation; cancel followed by a new client id is the available
/// replace sequence.
/// </para>
/// </remarks>
public sealed class PositionTrackingOrderRouter : IOrderRouter, IDisposable
{
    public const int DefaultMaxTrackedOrders = 4096;
    public const int MaxClientOrderIdLength = 128;
    public const long MaxExactTargetUnits = 9_007_199_254_740_992L;

    private readonly object _gate = new();
    private readonly IVirtualBook _book;
    private readonly InstrumentId _instrument;
    private readonly IClock _clock;
    private readonly IOrderRoutedStrategy _legacyStrategy;
    private readonly IAlertSink _alerts;
    private readonly int _maxTrackedOrders;
    private readonly Subject<OrderEvent> _events = new();
    private readonly Dictionary<string, TrackedOrder> _orders = new(StringComparer.Ordinal);

    private long _netPosition;
    private long _nextOrderSequence;
    private double? _referencePrice;
    private bool _disposed;

    public PositionTrackingOrderRouter(
        IVirtualBook book,
        InstrumentId instrument,
        IClock clock,
        IOrderRoutedStrategy legacyStrategy,
        IAlertSink alerts,
        int maxTrackedOrders = DefaultMaxTrackedOrders)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(legacyStrategy);
        ArgumentNullException.ThrowIfNull(alerts);
        if (instrument.IsNone)
            throw new ArgumentException("A bound instrument is required.", nameof(instrument));
        if (maxTrackedOrders <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxTrackedOrders),
                "The tracked-order bound must be positive.");

        _book = book;
        _instrument = instrument;
        _clock = clock;
        _legacyStrategy = legacyStrategy;
        _alerts = alerts;
        _maxTrackedOrders = maxTrackedOrders;
    }

    /// <summary>The instrument receiving every translated target.</summary>
    public InstrumentId Instrument => _instrument;

    /// <summary>The current signed legacy position, mapped 1:1 to virtual reference units.</summary>
    public long NetPosition
    {
        get
        {
            lock (_gate)
                return _netPosition;
        }
    }

    /// <summary>The latest valid reference price, or null before the first usable market event.</summary>
    public double? ReferencePrice
    {
        get
        {
            lock (_gate)
                return _referencePrice;
        }
    }

    /// <summary>A hot, non-replaying stream of synthesized legacy order events.</summary>
    public IObservable<OrderEvent> OrderEvents => _events;

    /// <summary>
    /// Updates the immediate-fill reference. Invalid or non-positive prices clear the reference and
    /// are reported through the host-mediated alert sink, preventing a fill against stale data.
    /// </summary>
    public bool TryUpdateReferencePrice(double referencePrice)
    {
        if (!double.IsFinite(referencePrice) || referencePrice <= 0d)
        {
            lock (_gate)
            {
                if (!_disposed)
                    _referencePrice = null;
            }

            TryAlert(
                "Legacy strategy reference price was not finite and positive; immediate fills are disabled until the next valid market event.",
                AlertLevel.Warning,
                "legacy-router-invalid-reference");
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
                return false;

            _referencePrice = referencePrice;
            return true;
        }
    }

    public async Task<OrderResult> PlaceOrderAsync(
        OrderRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request is null)
        {
            const string reason = "The legacy strategy submitted a null order request.";
            TryAlert(reason, AlertLevel.Warning, "legacy-router-rejection");
            return new OrderResult(string.Empty, null, OrderState.Rejected, reason);
        }

        OrderResult result;
        OrderEvent orderEvent;
        bool accepted;

        lock (_gate)
        {
            if (CanUseAsIdempotencyKey(request.ClientOrderId) &&
                _orders.TryGetValue(request.ClientOrderId, out var prior))
            {
                return prior.Result;
            }

            var rejectReason = ValidateForImmediateFill(request);
            var nextPosition = _netPosition;
            var signedQuantity = 0L;
            if (rejectReason is null)
            {
                signedQuantity = request.Side == OrderSide.Buy
                    ? request.Quantity
                    : -request.Quantity;

                try
                {
                    nextPosition = checked(_netPosition + signedQuantity);
                }
                catch (OverflowException)
                {
                    nextPosition = 0;
                    rejectReason = "The requested order would overflow the legacy signed position.";
                }

                if (rejectReason is null &&
                    (nextPosition > MaxExactTargetUnits || nextPosition < -MaxExactTargetUnits))
                {
                    rejectReason =
                        $"The requested target exceeds {MaxExactTargetUnits} units, the exact integer range of the virtual book.";
                }

                if (rejectReason is null)
                {
                    try
                    {
                        // Commit the declarative target before exposing a synthetic fill. This keeps
                        // router, legacy state, and book transactional if host validation rejects it,
                        // and preserves every transition when a fill callback submits recursively.
                        _book.SetTargetPosition(
                            _instrument,
                            nextPosition,
                            protectiveStopPrice: nextPosition == 0 ? null : request.StopPrice,
                            profitTargetPrice: nextPosition == 0 ? null : request.LimitPrice);
                        _netPosition = nextPosition;
                    }
                    catch (Exception ex)
                    {
                        rejectReason =
                            $"The virtual book rejected the legacy target ({ex.GetType().Name}).";
                    }
                }
            }

            accepted = rejectReason is null;
            var eventSide = Enum.IsDefined(request.Side) ? request.Side : OrderSide.Buy;
            var fillPrice = accepted ? _referencePrice : null;
            var state = accepted ? OrderState.Filled : OrderState.Rejected;

            result = new OrderResult(
                ClientOrderId: request.ClientOrderId ?? string.Empty,
                BrokerOrderId: null,
                State: state,
                RejectReason: rejectReason);
            orderEvent = new OrderEvent(
                TimestampUtc: _clock.UtcNow,
                ClientOrderId: request.ClientOrderId ?? string.Empty,
                BrokerOrderId: null,
                Side: eventSide,
                State: state,
                FilledQuantity: accepted ? request.Quantity : 0,
                AverageFillPrice: fillPrice,
                LastFillQuantity: accepted ? request.Quantity : 0,
                LastFillPrice: fillPrice,
                RejectReason: rejectReason,
                Liquidity: LiquidityFlag.Taker);

            if (CanUseAsIdempotencyKey(request.ClientOrderId) &&
                _orders.Count < _maxTrackedOrders)
            {
                _orders.Add(
                    request.ClientOrderId!,
                    new TrackedOrder(
                        result,
                        eventSide,
                        accepted ? signedQuantity : 0,
                        accepted ? request.Quantity : 0,
                        fillPrice,
                        accepted ? request.StopPrice : null,
                        accepted ? request.LimitPrice : null,
                        accepted ? ++_nextOrderSequence : 0));
            }
        }

        if (!accepted)
            TryAlert(result.RejectReason!, AlertLevel.Warning, "legacy-router-rejection");

        Publish(orderEvent);

        // Awaiting the callback inside PlaceOrderAsync preserves legacy strategies' expectation that
        // fill- or rejection-driven state has advanced before their submission call completes.
        await _legacyStrategy.OnOrderEventAsync(orderEvent, ct);

        return result;
    }

    public async Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        string? warning = null;
        AlertLevel warningLevel = AlertLevel.Information;
        OrderEvent? cancellation = null;
        lock (_gate)
        {
            if (_disposed)
            {
                warning = "The legacy strategy tried to cancel an order after its router was disposed.";
            }
            else if (string.IsNullOrWhiteSpace(clientOrderId))
            {
                warning = "The legacy strategy submitted an empty cancellation id.";
            }
            else if (_orders.TryGetValue(clientOrderId, out var tracked) &&
                     tracked.Result.State == OrderState.Filled &&
                     !tracked.Cancelled)
            {
                try
                {
                    var nextPosition = checked(_netPosition - tracked.SignedQuantity);
                    if (nextPosition > MaxExactTargetUnits || nextPosition < -MaxExactTargetUnits)
                    {
                        throw new InvalidOperationException(
                            "Cancellation would exceed the virtual book's exact integer range.");
                    }

                    var (protectiveStopPrice, profitTargetPrice) =
                        ProtectionsAfterCancellation(tracked, nextPosition);
                    _book.SetTargetPosition(
                        _instrument,
                        nextPosition,
                        protectiveStopPrice,
                        profitTargetPrice);
                    _netPosition = nextPosition;
                    tracked.Cancelled = true;
                    cancellation = new OrderEvent(
                        TimestampUtc: _clock.UtcNow,
                        ClientOrderId: clientOrderId,
                        BrokerOrderId: null,
                        Side: tracked.Side,
                        State: OrderState.Cancelled,
                        FilledQuantity: tracked.Quantity,
                        AverageFillPrice: tracked.FillPrice,
                        LastFillQuantity: 0,
                        LastFillPrice: null,
                        RejectReason: null,
                        Liquidity: LiquidityFlag.Taker);
                }
                catch (Exception ex)
                {
                    warning =
                        $"The virtual book rejected cancellation of legacy order '{clientOrderId}' ({ex.GetType().Name}).";
                    warningLevel = AlertLevel.Error;
                }
            }
        }

        if (warning is not null)
            TryAlert(warning, warningLevel, "legacy-router-cancel-rejection");

        if (cancellation is not null)
        {
            Publish(cancellation);
            await _legacyStrategy.OnOrderEventAsync(cancellation, ct);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _orders.Clear();
            _referencePrice = null;
        }

        try
        {
            _events.OnCompleted();
        }
        catch (Exception ex)
        {
            TryAlert(
                $"A legacy order-event observer failed during shutdown ({ex.GetType().Name}).",
                AlertLevel.Warning,
                "legacy-router-observer-fault");
        }
        finally
        {
            _events.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private string? ValidateForImmediateFill(OrderRequest request)
    {
        if (_disposed)
            return "The legacy order router is disposed.";
        if (string.IsNullOrWhiteSpace(request.ClientOrderId))
            return "Legacy client order ids must be non-empty.";
        if (request.ClientOrderId.Length > MaxClientOrderIdLength)
            return $"Legacy client order ids cannot exceed {MaxClientOrderIdLength} characters.";
        if (_orders.Count >= _maxTrackedOrders)
            return $"The bounded legacy order limit of {_maxTrackedOrders} unique ids was reached.";
        if (request.Contract is null)
            return "The legacy order does not identify a contract.";
        if (!Enum.IsDefined(request.Side))
            return $"Legacy order side '{request.Side}' is not supported.";
        if (!Enum.IsDefined(request.Type))
            return $"Legacy order type '{request.Type}' is not supported.";
        if (!Enum.IsDefined(request.TimeInForce))
            return $"Legacy time-in-force '{request.TimeInForce}' is not supported.";
        if (request.Quantity <= 0)
            return "Legacy order quantity must be positive.";
        if (_referencePrice is null)
            return "No valid market reference price is available for an immediate legacy fill.";
        if (request.Type is OrderType.Limit or OrderType.StopLimit && request.LimitPrice is null)
            return $"Legacy {request.Type} orders require a limit price.";
        if (request.Type is OrderType.Stop or OrderType.StopLimit && request.StopPrice is null)
            return $"Legacy {request.Type} orders require a stop price.";
        if (request.LimitPrice is { } limitPrice &&
            (!double.IsFinite(limitPrice) || limitPrice <= 0d))
            return "Legacy limit prices must be finite and positive.";
        if (request.StopPrice is { } stopPrice &&
            (!double.IsFinite(stopPrice) || stopPrice <= 0d))
            return "Legacy stop prices must be finite and positive.";

        return null;
    }

    private (double? ProtectiveStopPrice, double? ProfitTargetPrice) ProtectionsAfterCancellation(
        TrackedOrder cancelled,
        long nextPosition)
    {
        if (nextPosition == 0)
            return (null, null);

        TrackedOrder? latest = null;
        foreach (var candidate in _orders.Values)
        {
            if (ReferenceEquals(candidate, cancelled) ||
                candidate.Cancelled ||
                candidate.Result.State != OrderState.Filled ||
                candidate.Sequence <= (latest?.Sequence ?? 0))
            {
                continue;
            }

            latest = candidate;
        }

        return latest is null
            ? (null, null)
            : (latest.ProtectiveStopPrice, latest.ProfitTargetPrice);
    }

    private void Publish(OrderEvent orderEvent)
    {
        try
        {
            _events.OnNext(orderEvent);
        }
        catch (Exception ex)
        {
            TryAlert(
                $"A legacy order-event observer failed ({ex.GetType().Name}).",
                AlertLevel.Warning,
                "legacy-router-observer-fault");
        }
    }

    private void TryAlert(string message, AlertLevel level, string dedupeKey)
    {
        try
        {
            _alerts.Alert(message, level, dedupeKey);
        }
        catch
        {
            // Rejection reporting is best effort; a bounded alert sink must not turn an
            // unsupported legacy order into an exception escaping through the old router.
        }
    }

    private static bool CanUseAsIdempotencyKey(string? clientOrderId) =>
        !string.IsNullOrWhiteSpace(clientOrderId) &&
        clientOrderId.Length <= MaxClientOrderIdLength;

    private sealed class TrackedOrder(
        OrderResult result,
        OrderSide side,
        long signedQuantity,
        long quantity,
        double? fillPrice,
        double? protectiveStopPrice,
        double? profitTargetPrice,
        long sequence)
    {
        public OrderResult Result { get; } = result;
        public OrderSide Side { get; } = side;
        public long SignedQuantity { get; } = signedQuantity;
        public long Quantity { get; } = quantity;
        public double? FillPrice { get; } = fillPrice;
        public double? ProtectiveStopPrice { get; } = protectiveStopPrice;
        public double? ProfitTargetPrice { get; } = profitTargetPrice;
        public long Sequence { get; } = sequence;
        public bool Cancelled { get; set; }
    }
}
