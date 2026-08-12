using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using CoreOrderEvent = TradingTerminal.Core.Trading.OrderEvent;
using LedgerOrderEvent = TradingTerminal.Execution.Oms.OrderEvent;

namespace TradingTerminal.Execution.Oms;

/// <summary>Fault-as-value result for the explicit public Core order compatibility boundary.</summary>
public enum PublicOrderMappingFault : byte
{
    /// <summary>The value mapped without semantic loss.</summary>
    None = 0,

    /// <summary>A required source object is absent.</summary>
    MissingValue = 1,

    /// <summary>The public or canonical client-order id is invalid.</summary>
    InvalidClientOrderId = 2,

    /// <summary>The exact quantity is invalid or cannot be represented as public whole units.</summary>
    InvalidQuantity = 3,

    /// <summary>A price is absent, unexpected, non-positive, or outside the exact boundary scale.</summary>
    InvalidPrice = 4,

    /// <summary>An exact price cannot round-trip through the frozen public double representation.</summary>
    PriceNotRepresentable = 5,

    /// <summary>The order type is unknown to one side of the seam.</summary>
    UnsupportedOrderType = 6,

    /// <summary>The time-in-force is unknown to one side of the seam.</summary>
    UnsupportedTimeInForce = 7,

    /// <summary>The richer OMS state has no faithful public <see cref="OrderState"/> equivalent.</summary>
    UnsupportedLifecycleState = 8,

    /// <summary>The event kind has no faithful counterpart at the public boundary.</summary>
    UnsupportedEvent = 9,

    /// <summary>Event fields contradict its state or fill semantics.</summary>
    InconsistentEvent = 10,

    /// <summary>A typed causation or deduplication identity is invalid.</summary>
    InvalidEventIdentity = 11,
}

/// <summary>
/// Explicit compatibility mapper between the richer internal OMS and frozen public Core order
/// seams. Conversion is fail-closed: unsupported states, including Unknown, never become Rejected.
/// Public binary prices are quantized only here through <c>ScaledValueMath</c> (roadmap section 6.2).
/// </summary>
public static class PublicOrderSeamMapper
{
    /// <summary>Maps a public order type to its canonical OMS equivalent.</summary>
    public static bool TryFromPublicOrderType(
        OrderType source,
        out CanonicalOrderType result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case OrderType.Market:
                result = CanonicalOrderType.Market;
                break;
            case OrderType.Limit:
                result = CanonicalOrderType.Limit;
                break;
            case OrderType.Stop:
                result = CanonicalOrderType.Stop;
                break;
            case OrderType.StopLimit:
                result = CanonicalOrderType.StopLimit;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedOrderType;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>Maps a canonical order type to the frozen public Core equivalent.</summary>
    public static bool TryToPublicOrderType(
        CanonicalOrderType source,
        out OrderType result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case CanonicalOrderType.Market:
                result = OrderType.Market;
                break;
            case CanonicalOrderType.Limit:
                result = OrderType.Limit;
                break;
            case CanonicalOrderType.Stop:
                result = OrderType.Stop;
                break;
            case CanonicalOrderType.StopLimit:
                result = OrderType.StopLimit;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedOrderType;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>Maps public time-in-force without defaulting or downgrade.</summary>
    public static bool TryFromPublicTimeInForce(
        TimeInForce source,
        out CanonicalTimeInForce result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case TimeInForce.Day:
                result = CanonicalTimeInForce.Day;
                break;
            case TimeInForce.Gtc:
                result = CanonicalTimeInForce.GoodTillCancelled;
                break;
            case TimeInForce.Ioc:
                result = CanonicalTimeInForce.ImmediateOrCancel;
                break;
            case TimeInForce.Fok:
                result = CanonicalTimeInForce.FillOrKill;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedTimeInForce;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>Maps canonical time-in-force to the frozen public Core equivalent.</summary>
    public static bool TryToPublicTimeInForce(
        CanonicalTimeInForce source,
        out TimeInForce result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case CanonicalTimeInForce.Day:
                result = TimeInForce.Day;
                break;
            case CanonicalTimeInForce.GoodTillCancelled:
                result = TimeInForce.Gtc;
                break;
            case CanonicalTimeInForce.ImmediateOrCancel:
                result = TimeInForce.Ioc;
                break;
            case CanonicalTimeInForce.FillOrKill:
                result = TimeInForce.Fok;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedTimeInForce;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>
    /// Quantizes the frozen public request's binary prices into exact canonical terms at the
    /// explicitly selected boundary scale. No sizing, notional, average-price, or fee arithmetic is
    /// performed here.
    /// </summary>
    public static bool TryFromPublicRequest(
        OrderRequest? source,
        byte priceScale,
        out ClientOrderId clientOrderId,
        out CanonicalOrderTerms terms,
        out PublicOrderMappingFault fault)
    {
        clientOrderId = default;
        terms = default;
        if (source is null)
        {
            fault = PublicOrderMappingFault.MissingValue;
            return false;
        }

        clientOrderId = new ClientOrderId(source.ClientOrderId);
        if (!clientOrderId.IsValid)
        {
            fault = PublicOrderMappingFault.InvalidClientOrderId;
            return false;
        }
        if (source.Quantity <= 0)
        {
            fault = PublicOrderMappingFault.InvalidQuantity;
            return false;
        }
        if (!TryFromPublicOrderType(source.Type, out var orderType, out fault) ||
            !TryFromPublicTimeInForce(source.TimeInForce, out var timeInForce, out fault) ||
            !TryFromPublicPrice(source.LimitPrice, priceScale, out var limitPrice, out fault) ||
            !TryFromPublicPrice(source.StopPrice, priceScale, out var stopPrice, out fault))
            return false;

        terms = new CanonicalOrderTerms(
            source.Side,
            orderType,
            timeInForce,
            ScaledQuantity.FromWhole(source.Quantity),
            limitPrice,
            stopPrice);
        fault = MapDomainFault(terms.Validate());
        return fault == PublicOrderMappingFault.None;
    }

    /// <summary>
    /// Converts exact canonical terms to a public request. Price conversion is confined to this
    /// boundary and must round-trip through <c>ScaledValueMath</c> at the original exact scale.
    /// </summary>
    public static bool TryToPublicRequest(
        ClientOrderId clientOrderId,
        Contract? contract,
        CanonicalOrderTerms terms,
        out OrderRequest? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (!clientOrderId.IsValid)
        {
            fault = PublicOrderMappingFault.InvalidClientOrderId;
            return false;
        }
        if (contract is null)
        {
            fault = PublicOrderMappingFault.MissingValue;
            return false;
        }

        fault = MapDomainFault(terms.Validate());
        if (fault != PublicOrderMappingFault.None)
            return false;
        if (!terms.Quantity.TryGetWholeUnits(out var quantity) || quantity <= 0)
        {
            fault = PublicOrderMappingFault.InvalidQuantity;
            return false;
        }
        if (!TryToPublicOrderType(terms.OrderType, out var orderType, out fault) ||
            !TryToPublicTimeInForce(terms.TimeInForce, out var timeInForce, out fault) ||
            !TryToPublicPrice(terms.LimitPrice, out var limitPrice, out fault) ||
            !TryToPublicPrice(terms.StopPrice, out var stopPrice, out fault))
            return false;

        result = new OrderRequest(
            clientOrderId.Value,
            contract,
            terms.Side,
            orderType,
            quantity,
            limitPrice,
            stopPrice,
            timeInForce);
        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>
    /// Maps only lifecycle states with a faithful public representation. Unknown, Reconciling,
    /// PendingCancel, PendingReplace, Expired, and Reconciled return an explicit fault.
    /// </summary>
    public static bool TryToPublicOrderState(
        OrderLifecycleState source,
        out OrderState result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case OrderLifecycleState.Releasing:
            case OrderLifecycleState.Acknowledging:
                result = OrderState.PendingNew;
                break;
            case OrderLifecycleState.Working:
                result = OrderState.Working;
                break;
            case OrderLifecycleState.PartiallyFilled:
                result = OrderState.PartiallyFilled;
                break;
            case OrderLifecycleState.Filled:
                result = OrderState.Filled;
                break;
            case OrderLifecycleState.Cancelled:
                result = OrderState.Cancelled;
                break;
            case OrderLifecycleState.Rejected:
                result = OrderState.Rejected;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedLifecycleState;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>Maps a public lifecycle state to the corresponding venue-facing OMS state.</summary>
    public static bool TryFromPublicOrderState(
        OrderState source,
        out OrderLifecycleState result,
        out PublicOrderMappingFault fault)
    {
        switch (source)
        {
            case OrderState.PendingNew:
                result = OrderLifecycleState.Acknowledging;
                break;
            case OrderState.Working:
                result = OrderLifecycleState.Working;
                break;
            case OrderState.PartiallyFilled:
                result = OrderLifecycleState.PartiallyFilled;
                break;
            case OrderState.Filled:
                result = OrderLifecycleState.Filled;
                break;
            case OrderState.Cancelled:
                result = OrderLifecycleState.Cancelled;
                break;
            case OrderState.Rejected:
                result = OrderLifecycleState.Rejected;
                break;
            default:
                result = default;
                fault = PublicOrderMappingFault.UnsupportedLifecycleState;
                return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>
    /// Converts a frozen public callback into a typed venue callback. The public last-fill price is
    /// quantized once; cumulative economics are deliberately not recomputed at this boundary.
    /// </summary>
    public static bool TryFromPublicOrderEvent(
        CoreOrderEvent? source,
        CausationId causationId,
        DeduplicationKey deduplicationKey,
        byte priceScale,
        ScaledMoney fee,
        out VenueEvent? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (source is null)
        {
            fault = PublicOrderMappingFault.MissingValue;
            return false;
        }

        var clientOrderId = new ClientOrderId(source.ClientOrderId);
        if (!clientOrderId.IsValid)
        {
            fault = PublicOrderMappingFault.InvalidClientOrderId;
            return false;
        }
        if (!causationId.IsValid || !deduplicationKey.IsValid)
        {
            fault = PublicOrderMappingFault.InvalidEventIdentity;
            return false;
        }
        if (!Enum.IsDefined(source.Side) || source.TimestampUtc.Kind != DateTimeKind.Utc)
        {
            fault = PublicOrderMappingFault.InconsistentEvent;
            return false;
        }

        BrokerOrderId? brokerOrderId = null;
        if (source.BrokerOrderId is not null)
        {
            brokerOrderId = new BrokerOrderId(source.BrokerOrderId);
            if (!brokerOrderId.Value.IsValid)
            {
                fault = PublicOrderMappingFault.InvalidEventIdentity;
                return false;
            }
        }

        FillExecution? fill = null;
        VenueEventKind kind;
        if (source.LastFillQuantity > 0)
        {
            fault = PublicOrderMappingFault.None;
            if (source.State is not (OrderState.PartiallyFilled or OrderState.Filled) ||
                source.FilledQuantity < source.LastFillQuantity ||
                !source.LastFillPrice.HasValue ||
                !TryFromPublicPrice(source.LastFillPrice, priceScale, out var fillPrice, out fault) ||
                !fillPrice.HasValue)
                return SetInconsistentUnlessPriceFault(ref fault);

            fill = new FillExecution(
                ScaledQuantity.FromWhole(source.LastFillQuantity),
                fillPrice.Value,
                fee,
                source.Liquidity);
            if (!fill.Value.IsValid)
            {
                fault = PublicOrderMappingFault.InconsistentEvent;
                return false;
            }
            kind = VenueEventKind.Fill;
        }
        else
        {
            if (source.LastFillQuantity < 0 || source.LastFillPrice.HasValue)
            {
                fault = PublicOrderMappingFault.InconsistentEvent;
                return false;
            }

            switch (source.State)
            {
                case OrderState.Working:
                    kind = VenueEventKind.Acknowledged;
                    break;
                case OrderState.Cancelled:
                    kind = VenueEventKind.Cancelled;
                    break;
                case OrderState.Rejected:
                    kind = VenueEventKind.Rejected;
                    break;
                default:
                    fault = PublicOrderMappingFault.UnsupportedEvent;
                    return false;
            }
        }

        result = new VenueEvent(
            kind,
            clientOrderId,
            brokerOrderId,
            null,
            fill,
            null,
            source.TimestampUtc,
            causationId,
            deduplicationKey,
            source.RejectReason);
        fault = PublicOrderMappingFault.None;
        return true;
    }

    /// <summary>
    /// Converts a typed venue callback to the public event seam using caller-supplied cumulative
    /// exact values. The mapper never calculates an average or adds fill quantities.
    /// </summary>
    public static bool TryToPublicOrderEvent(
        VenueEvent? source,
        OrderSide side,
        OrderLifecycleState state,
        ScaledQuantity cumulativeFilledQuantity,
        ScaledPrice? averageFillPrice,
        out CoreOrderEvent? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (source is null)
        {
            fault = PublicOrderMappingFault.MissingValue;
            return false;
        }

        return TryToPublicOrderEventCore(
            source.ClientOrderId,
            source.BrokerOrderId,
            source.Kind,
            source.Fill,
            source.OccurredAtUtc,
            source.Reason,
            side,
            state,
            cumulativeFilledQuantity,
            averageFillPrice,
            out result,
            out fault);
    }

    /// <summary>
    /// Converts an immutable OMS ledger event to the frozen public event seam. Events and states
    /// without faithful public meaning fail explicitly; OutcomeUnknown can therefore never surface as
    /// a public rejection.
    /// </summary>
    public static bool TryToPublicOrderEvent(
        LedgerOrderEvent? source,
        OrderSide side,
        ScaledQuantity cumulativeFilledQuantity,
        ScaledPrice? averageFillPrice,
        out CoreOrderEvent? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (source is null)
        {
            fault = PublicOrderMappingFault.MissingValue;
            return false;
        }
        if (!TryMapLedgerEventKind(source.Kind, out var eventKind))
        {
            fault = PublicOrderMappingFault.UnsupportedEvent;
            return false;
        }

        return TryToPublicOrderEventCore(
            source.AggregateId,
            source.BrokerOrderId,
            eventKind,
            source.Fill,
            source.OccurredAtUtc,
            source.Reason,
            side,
            source.StateAfter,
            cumulativeFilledQuantity,
            averageFillPrice,
            out result,
            out fault);
    }

    private static bool TryToPublicOrderEventCore(
        ClientOrderId clientOrderId,
        BrokerOrderId? brokerOrderId,
        VenueEventKind eventKind,
        FillExecution? fill,
        DateTime occurredAtUtc,
        string? reason,
        OrderSide side,
        OrderLifecycleState state,
        ScaledQuantity cumulativeFilledQuantity,
        ScaledPrice? averageFillPrice,
        out CoreOrderEvent? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (!clientOrderId.IsValid || brokerOrderId is { IsValid: false })
        {
            fault = PublicOrderMappingFault.InvalidEventIdentity;
            return false;
        }
        if (!Enum.IsDefined(eventKind) ||
            !Enum.IsDefined(side) ||
            occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            fault = PublicOrderMappingFault.InconsistentEvent;
            return false;
        }
        if (!TryToPublicOrderState(state, out var publicState, out fault))
            return false;
        if (!cumulativeFilledQuantity.TryGetWholeUnits(out var cumulativeFilled) || cumulativeFilled < 0)
        {
            fault = PublicOrderMappingFault.InvalidQuantity;
            return false;
        }

        long lastFillQuantity = 0;
        double? lastFillPrice = null;
        var liquidity = LiquidityFlag.Taker;
        if (fill.HasValue)
        {
            if (eventKind != VenueEventKind.Fill ||
                !fill.Value.IsValid ||
                !fill.Value.Quantity.TryGetWholeUnits(out lastFillQuantity) ||
                lastFillQuantity > cumulativeFilled ||
                !TryToPublicPrice(fill.Value.Price, out var publicFillPrice, out fault))
            {
                if (fault == PublicOrderMappingFault.None)
                    fault = PublicOrderMappingFault.InconsistentEvent;
                return false;
            }

            lastFillPrice = publicFillPrice;
            liquidity = fill.Value.Liquidity;
        }
        else if (eventKind == VenueEventKind.Fill)
        {
            fault = PublicOrderMappingFault.InconsistentEvent;
            return false;
        }

        double? publicAverage = null;
        if (cumulativeFilled > 0)
        {
            if (!averageFillPrice.HasValue ||
                !TryToPublicPrice(averageFillPrice.Value, out var convertedAverage, out fault))
                return false;
            publicAverage = convertedAverage;
        }
        else if (averageFillPrice.HasValue)
        {
            fault = PublicOrderMappingFault.InconsistentEvent;
            return false;
        }

        result = new CoreOrderEvent(
            occurredAtUtc,
            clientOrderId.Value,
            brokerOrderId?.Value,
            side,
            publicState,
            cumulativeFilled,
            publicAverage,
            lastFillQuantity,
            lastFillPrice,
            reason,
            liquidity);
        fault = PublicOrderMappingFault.None;
        return true;
    }

    private static bool TryMapLedgerEventKind(OrderEventKind source, out VenueEventKind result)
    {
        switch (source)
        {
            case OrderEventKind.VenueAcknowledged:
                result = VenueEventKind.Acknowledged;
                return true;
            case OrderEventKind.FillReceived:
                result = VenueEventKind.Fill;
                return true;
            case OrderEventKind.CancelConfirmed:
                result = VenueEventKind.Cancelled;
                return true;
            case OrderEventKind.ReplaceConfirmed:
                result = VenueEventKind.Replaced;
                return true;
            case OrderEventKind.RiskRejected:
            case OrderEventKind.ValidationRejected:
            case OrderEventKind.VenueRejected:
                result = VenueEventKind.Rejected;
                return true;
            case OrderEventKind.SendFailedBeforeAcceptance:
                result = VenueEventKind.FailedBeforeAcceptance;
                return true;
            case OrderEventKind.OutcomeUnknown:
                result = VenueEventKind.OutcomeUnknown;
                return true;
            case OrderEventKind.Expired:
                result = VenueEventKind.Expired;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool TryFromPublicPrice(
        double? source,
        byte scale,
        out ScaledPrice? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (!source.HasValue)
        {
            fault = PublicOrderMappingFault.None;
            return true;
        }
        if (source.Value <= 0 || !ScaledValueMath.TryQuantizeDouble(source.Value, scale, out var coefficient))
        {
            fault = PublicOrderMappingFault.InvalidPrice;
            return false;
        }

        result = new ScaledPrice(coefficient, scale);
        fault = PublicOrderMappingFault.None;
        return true;
    }

    private static bool TryToPublicPrice(
        ScaledPrice? source,
        out double? result,
        out PublicOrderMappingFault fault)
    {
        result = null;
        if (!source.HasValue)
        {
            fault = PublicOrderMappingFault.None;
            return true;
        }
        if (!TryToPublicPrice(source.Value, out var value, out fault))
            return false;
        result = value;
        return true;
    }

    private static bool TryToPublicPrice(
        ScaledPrice source,
        out double result,
        out PublicOrderMappingFault fault)
    {
        result = 0;
        if (!source.IsValid || source.Coefficient <= 0)
        {
            fault = PublicOrderMappingFault.InvalidPrice;
            return false;
        }

        var exactDecimal = (decimal)source.Coefficient / (decimal)ScaledValueMath.Pow10(source.Scale);
        result = (double)exactDecimal;
        if (!ScaledValueMath.TryQuantizeDouble(result, source.Scale, out var roundTrip) ||
            roundTrip != source.Coefficient)
        {
            result = 0;
            fault = PublicOrderMappingFault.PriceNotRepresentable;
            return false;
        }

        fault = PublicOrderMappingFault.None;
        return true;
    }

    private static PublicOrderMappingFault MapDomainFault(OrderDomainFault fault) => fault switch
    {
        OrderDomainFault.None => PublicOrderMappingFault.None,
        OrderDomainFault.InvalidQuantity => PublicOrderMappingFault.InvalidQuantity,
        OrderDomainFault.InvalidPriceTerms => PublicOrderMappingFault.InvalidPrice,
        OrderDomainFault.UnsupportedOrderType => PublicOrderMappingFault.UnsupportedOrderType,
        OrderDomainFault.UnsupportedTimeInForce => PublicOrderMappingFault.UnsupportedTimeInForce,
        _ => PublicOrderMappingFault.InconsistentEvent,
    };

    private static bool SetInconsistentUnlessPriceFault(ref PublicOrderMappingFault fault)
    {
        if (fault == PublicOrderMappingFault.None)
            fault = PublicOrderMappingFault.InconsistentEvent;
        return false;
    }
}
