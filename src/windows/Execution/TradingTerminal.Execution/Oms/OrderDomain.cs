using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution.Oms;

/// <summary>Canonical order types required by roadmap section 6.3.</summary>
public enum CanonicalOrderType : byte
{
    /// <summary>Execute at the venue's available price.</summary>
    Market = 0,

    /// <summary>Execute only at the limit or better.</summary>
    Limit = 1,

    /// <summary>Activate when the stop is reached and then execute as a market order.</summary>
    Stop = 2,

    /// <summary>Activate when the stop is reached and then execute subject to the limit.</summary>
    StopLimit = 3,
}

/// <summary>Canonical time-in-force values required by roadmap section 6.3.</summary>
public enum CanonicalTimeInForce : byte
{
    /// <summary>Valid for the current venue session.</summary>
    Day = 0,

    /// <summary>Good until explicitly cancelled or venue expiry.</summary>
    GoodTillCancelled = 1,

    /// <summary>Fill immediately to the available extent and cancel the remainder.</summary>
    ImmediateOrCancel = 2,

    /// <summary>Fill the entire order immediately or cancel it.</summary>
    FillOrKill = 3,
}

/// <summary>Bit set describing order types a venue can represent without downgrade.</summary>
[Flags]
public enum SupportedOrderTypes : byte
{
    /// <summary>No type is supported.</summary>
    None = 0,

    /// <summary>Market orders.</summary>
    Market = 1 << 0,

    /// <summary>Limit orders.</summary>
    Limit = 1 << 1,

    /// <summary>Stop orders.</summary>
    Stop = 1 << 2,

    /// <summary>Stop-limit orders.</summary>
    StopLimit = 1 << 3,

    /// <summary>Every slice-1 canonical type.</summary>
    All = Market | Limit | Stop | StopLimit,
}

/// <summary>Bit set describing time-in-force values a venue can represent without downgrade.</summary>
[Flags]
public enum SupportedTimeInForce : byte
{
    /// <summary>No time in force is supported.</summary>
    None = 0,

    /// <summary>Day.</summary>
    Day = 1 << 0,

    /// <summary>Good till cancelled.</summary>
    GoodTillCancelled = 1 << 1,

    /// <summary>Immediate or cancel.</summary>
    ImmediateOrCancel = 1 << 2,

    /// <summary>Fill or kill.</summary>
    FillOrKill = 1 << 3,

    /// <summary>Every slice-1 canonical value.</summary>
    All = Day | GoodTillCancelled | ImmediateOrCancel | FillOrKill,
}

/// <summary>Fault-as-value structural validation for a canonical order.</summary>
public enum OrderDomainFault : byte
{
    /// <summary>The value is valid.</summary>
    None = 0,

    /// <summary>A required strongly typed identity is absent or invalid.</summary>
    InvalidIdentity = 1,

    /// <summary>The exact quantity is invalid, non-positive, or not a whole quantity.</summary>
    InvalidQuantity = 2,

    /// <summary>Required price terms are absent, invalid, non-positive, or unexpectedly present.</summary>
    InvalidPriceTerms = 3,

    /// <summary>The embedded trade intent is inconsistent with the order instruction.</summary>
    InvalidTradeIntent = 4,

    /// <summary>The venue cannot represent the order type without downgrade.</summary>
    UnsupportedOrderType = 5,

    /// <summary>The venue cannot represent the time in force without downgrade.</summary>
    UnsupportedTimeInForce = 6,

    /// <summary>An order-side, type, time-in-force, quantity-mode, or liquidity value is undefined.</summary>
    InvalidClassification = 7,
}

/// <summary>Exact canonical native-order terms from roadmap sections 6.2 and 6.3.</summary>
public readonly record struct CanonicalOrderTerms(
    OrderSide Side,
    CanonicalOrderType OrderType,
    CanonicalTimeInForce TimeInForce,
    ScaledQuantity Quantity,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice)
{
    /// <summary>Validates exact quantity and price-shape invariants without rounding or defaulting.</summary>
    public OrderDomainFault Validate()
    {
        if (!Enum.IsDefined(Side) ||
            !Enum.IsDefined(OrderType) ||
            !Enum.IsDefined(TimeInForce))
        {
            return OrderDomainFault.InvalidClassification;
        }

        if (!Quantity.TryGetWholeUnits(out var quantity) || quantity <= 0)
            return OrderDomainFault.InvalidQuantity;

        if (LimitPrice.HasValue && (!LimitPrice.Value.IsValid || LimitPrice.Value.Coefficient <= 0) ||
            StopPrice.HasValue && (!StopPrice.Value.IsValid || StopPrice.Value.Coefficient <= 0))
            return OrderDomainFault.InvalidPriceTerms;

        var pricesAreValid = OrderType switch
        {
            CanonicalOrderType.Market => !LimitPrice.HasValue && !StopPrice.HasValue,
            CanonicalOrderType.Limit => LimitPrice.HasValue && !StopPrice.HasValue,
            CanonicalOrderType.Stop => !LimitPrice.HasValue && StopPrice.HasValue,
            CanonicalOrderType.StopLimit => LimitPrice.HasValue && StopPrice.HasValue,
            _ => false,
        };
        return pricesAreValid ? OrderDomainFault.None : OrderDomainFault.InvalidPriceTerms;
    }
}

/// <summary>
/// One internal economic instruction. It wraps the existing <see cref="TradeIntent"/> rather
/// than duplicating intent, exact-value, or policy concepts (ADR D1/D4 and roadmap section 6.2).
/// </summary>
public sealed record CanonicalOrderInstruction(
    OrderIdentity Identity,
    TradeIntent TradeIntent,
    CanonicalOrderTerms Terms)
{
    /// <summary>Validates the instruction's identities, trade intent, and exact order terms.</summary>
    public OrderDomainFault Validate()
    {
        if (!Identity.IsValid)
            return OrderDomainFault.InvalidIdentity;

        if (!Enum.IsDefined(TradeIntent.QuantityMode) ||
            TradeIntent.Instrument.IsNone ||
            !TradeIntent.SignedUnits.IsValid ||
            !TradeIntent.EstimatedRoundTripCostPerUnit.IsValid ||
            TradeIntent.EstimatedRoundTripCostPerUnit.Coefficient < 0 ||
            TradeIntent.ProtectiveStopPrice.HasValue &&
                (!TradeIntent.ProtectiveStopPrice.Value.IsValid ||
                 TradeIntent.ProtectiveStopPrice.Value.Coefficient <= 0) ||
            TradeIntent.ProfitTargetPrice.HasValue &&
                (!TradeIntent.ProfitTargetPrice.Value.IsValid ||
                 TradeIntent.ProfitTargetPrice.Value.Coefficient <= 0) ||
            string.IsNullOrWhiteSpace(TradeIntent.StrategyId) ||
            string.IsNullOrWhiteSpace(TradeIntent.PolicyVersion))
            return OrderDomainFault.InvalidTradeIntent;

        return Terms.Validate();
    }
}

/// <summary>Immutable capabilities of the deterministic slice-1 venue.</summary>
public readonly record struct VenueCapabilities(
    SupportedOrderTypes OrderTypes,
    SupportedTimeInForce TimeInForce)
{
    /// <summary>The simulator's default support for all explicitly modeled slice-1 capabilities.</summary>
    public static VenueCapabilities All => new(SupportedOrderTypes.All, SupportedTimeInForce.All);

    /// <summary>Rejects a term the venue cannot faithfully represent; no downgrade is attempted.</summary>
    public OrderDomainFault Validate(in CanonicalOrderTerms terms)
    {
        var structuralFault = terms.Validate();
        if (structuralFault != OrderDomainFault.None)
            return structuralFault;

        var typeFlag = terms.OrderType switch
        {
            CanonicalOrderType.Market => SupportedOrderTypes.Market,
            CanonicalOrderType.Limit => SupportedOrderTypes.Limit,
            CanonicalOrderType.Stop => SupportedOrderTypes.Stop,
            CanonicalOrderType.StopLimit => SupportedOrderTypes.StopLimit,
            _ => SupportedOrderTypes.None,
        };
        if ((OrderTypes & typeFlag) == 0)
            return OrderDomainFault.UnsupportedOrderType;

        var tifFlag = terms.TimeInForce switch
        {
            CanonicalTimeInForce.Day => SupportedTimeInForce.Day,
            CanonicalTimeInForce.GoodTillCancelled => SupportedTimeInForce.GoodTillCancelled,
            CanonicalTimeInForce.ImmediateOrCancel => SupportedTimeInForce.ImmediateOrCancel,
            CanonicalTimeInForce.FillOrKill => SupportedTimeInForce.FillOrKill,
            _ => SupportedTimeInForce.None,
        };
        return (TimeInForce & tifFlag) == 0
            ? OrderDomainFault.UnsupportedTimeInForce
            : OrderDomainFault.None;
    }
}

/// <summary>One immutable exact simulated fill attached to an order event.</summary>
public readonly record struct FillExecution(
    ScaledQuantity Quantity,
    ScaledPrice Price,
    ScaledMoney Fee,
    LiquidityFlag Liquidity)
{
    /// <summary>Gets whether quantity, price, and fee are representable exact values.</summary>
    public bool IsValid =>
        Enum.IsDefined(Liquidity) &&
        Quantity.TryGetWholeUnits(out var units) && units > 0 &&
        Price.IsValid && Price.Coefficient > 0 &&
        Fee.IsValid && Fee.Coefficient >= 0;
}

/// <summary>Pure binding between a versioned risk snapshot and exact canonical order economics.</summary>
internal static class OrderRiskBinding
{
    internal static bool MatchesInstruction(
        in RiskInputSnapshot input,
        CanonicalOrderInstruction instruction) =>
        input.Intent == instruction.TradeIntent &&
        MatchesTerms(input, instruction, instruction.Terms, ScaledQuantity.Zero);

    internal static bool MatchesTerms(
        in RiskInputSnapshot input,
        CanonicalOrderInstruction instruction,
        in CanonicalOrderTerms terms,
        in ScaledQuantity alreadyFilled)
    {
        if (input.Intent.Instrument != instruction.TradeIntent.Instrument ||
            !input.PositionBefore.TryGetWholeUnits(out var currentUnits) ||
            !input.Intent.SignedUnits.TryGetWholeUnits(out var intentUnits) ||
            !terms.Quantity.TryGetWholeUnits(out var requestedUnits) ||
            !alreadyFilled.TryGetWholeUnits(out var filledUnits) ||
            filledUnits < 0 ||
            filledUnits >= requestedUnits)
        {
            return false;
        }

        long signedOrderUnits;
        try
        {
            signedOrderUnits = input.Intent.QuantityMode == TradeIntentQuantityMode.TargetPosition
                ? checked(intentUnits - currentUnits)
                : intentUnits;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (signedOrderUnits == 0)
            return false;
        var magnitude = signedOrderUnits < 0 ? -(Int128)signedOrderUnits : signedOrderUnits;
        if (magnitude != requestedUnits - filledUnits)
            return false;

        return signedOrderUnits > 0
            ? terms.Side == OrderSide.Buy
            : terms.Side == OrderSide.Sell;
    }
}
