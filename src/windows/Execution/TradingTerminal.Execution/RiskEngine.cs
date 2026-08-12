using System.Collections.ObjectModel;

namespace TradingTerminal.Execution;

/// <summary>Whether the complete attempted intent was admitted by pre-trade risk.</summary>
public enum RiskDecisionOutcome : byte
{
    /// <summary>The complete intent may proceed unchanged.</summary>
    Accepted = 0,

    /// <summary>The complete intent is refused; it is never clamped into a smaller instruction.</summary>
    Rejected = 1,
}

/// <summary>Stable, combinable reason codes recorded for every pre-trade decision.</summary>
[Flags]
public enum RiskReasonCode : ushort
{
    /// <summary>No risk reason rejected the intent.</summary>
    None = 0,

    /// <summary>The caller's exact snapshot is incomplete or internally inconsistent.</summary>
    InvalidSnapshot = 1 << 0,

    /// <summary>An exact intermediate exceeded the supported coefficient/scale range.</summary>
    ArithmeticOverflow = 1 << 1,

    /// <summary>The run-local manual kill switch has been tripped.</summary>
    KillSwitchTripped = 1 << 2,

    /// <summary>The combined realized and mark-to-market loss reached the daily limit.</summary>
    DailyLossLimitExceeded = 1 << 3,

    /// <summary>The target-to-current order delta exceeds the maximum order quantity.</summary>
    MaximumOrderQuantityExceeded = 1 << 4,

    /// <summary>The target-to-current order delta exceeds the maximum order notional.</summary>
    MaximumOrderNotionalExceeded = 1 << 5,

    /// <summary>The projected absolute instrument position exceeds its cap.</summary>
    MaximumAbsolutePositionExceeded = 1 << 6,

    /// <summary>The projected account gross exposure exceeds its cap.</summary>
    MaximumGrossExposureExceeded = 1 << 7,
}

/// <summary>
/// Exact caller-owned inputs captured before one <see cref="TradeIntent"/> is admitted. The UTC
/// risk day is explicit data rather than an engine clock; realized and mark-to-market PnL are kept
/// separate in the record and combined only for the daily-limit comparison.
/// </summary>
/// <param name="Intent">The complete attempted intent, retained even when rejected.</param>
/// <param name="PositionBefore">The current signed whole-unit position in the intent instrument.</param>
/// <param name="ReferencePrice">The exact risk mark used for notional and exposure.</param>
/// <param name="ContractMultiplier">The exact instrument contract multiplier.</param>
/// <param name="GrossExposureBefore">Exact account gross exposure before the intent.</param>
/// <param name="DailyRealizedPnl">Exact realized PnL since the UTC risk-day opening baseline.</param>
/// <param name="DailyMarkToMarketPnl">Exact open-position mark-to-market PnL change for the day.</param>
/// <param name="RiskDay">The deterministic UTC risk day supplied by the backtest tape.</param>
/// <param name="IsComplete">Whether every boundary value was represented exactly.</param>
public readonly record struct RiskInputSnapshot(
    TradeIntent Intent,
    ScaledQuantity PositionBefore,
    ScaledPrice ReferencePrice,
    ScaledRatio ContractMultiplier,
    ScaledMoney GrossExposureBefore,
    ScaledMoney DailyRealizedPnl,
    ScaledMoney DailyMarkToMarketPnl,
    DateOnly RiskDay,
    bool IsComplete = true);

/// <summary>Exact position and notional exposure on one side of a pre-trade decision.</summary>
/// <param name="Position">Signed instrument position.</param>
/// <param name="InstrumentExposure">Absolute notional exposure in the intent instrument.</param>
/// <param name="GrossExposure">Absolute notional exposure across the account.</param>
public readonly record struct RiskExposureSnapshot(
    ScaledQuantity Position,
    ScaledMoney InstrumentExposure,
    ScaledMoney GrossExposure);

/// <summary>
/// One immutable ADR D6/D7 pre-trade explanation. Policy values are copied into the record, so
/// replacing the engine's current policy cannot rewrite the reason for an earlier intent.
/// </summary>
/// <param name="PolicyId">Policy id used for this decision.</param>
/// <param name="PolicyVersion">Policy version used for this decision.</param>
/// <param name="PolicyHash">SHA-256 binding the exact policy content used for this decision.</param>
/// <param name="PolicyLimits">Exact policy limits used for this decision.</param>
/// <param name="Input">Complete attempted intent and exact risk inputs.</param>
/// <param name="Outcome">Accepted or rejected without modification.</param>
/// <param name="ReasonCodes">Stable flags explaining every observed rejection condition.</param>
/// <param name="SignedOrderQuantity">Exact target-to-current order delta.</param>
/// <param name="OrderNotional">Absolute exact notional of that order delta.</param>
/// <param name="ExposureBefore">Exposure before the complete attempted intent.</param>
/// <param name="ExposureAfter">Projected exposure after the complete attempted intent.</param>
public readonly record struct RiskDecisionRecord(
    string PolicyId,
    string PolicyVersion,
    string PolicyHash,
    RiskLimits PolicyLimits,
    RiskInputSnapshot Input,
    RiskDecisionOutcome Outcome,
    RiskReasonCode ReasonCodes,
    ScaledQuantity SignedOrderQuantity,
    ScaledMoney OrderNotional,
    RiskExposureSnapshot ExposureBefore,
    RiskExposureSnapshot ExposureAfter)
{
    /// <summary>Gets whether the complete attempted intent may proceed unchanged.</summary>
    public bool IsAccepted => Outcome == RiskDecisionOutcome.Accepted && ReasonCodes == RiskReasonCode.None;
}

/// <summary>
/// Deterministic run-local pre-trade state for the ADR D6 private execution boundary. The engine
/// has no I/O, clock, randomness, threads, or process-wide state. Its only transitions are explicit
/// policy replacement, a one-way manual kill switch, UTC-day loss latching, and append-only records.
/// </summary>
public sealed class RiskEngine
{
    private readonly List<RiskDecisionRecord> _decisions = [];
    private readonly ReadOnlyCollection<RiskDecisionRecord> _decisionView;
    private RiskPolicy _policy;
    private DateOnly _dailyLossDay;
    private bool _hasDailyLossDay;
    private bool _dailyLossTripped;
    private bool _killSwitchTripped;

    /// <summary>Creates a run-local engine from one already validated immutable policy.</summary>
    public RiskEngine(RiskPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _decisionView = _decisions.AsReadOnly();
    }

    /// <summary>Gets the policy used for the next decision.</summary>
    public RiskPolicy CurrentPolicy => _policy;

    /// <summary>Gets whether the one-way manual kill switch has been tripped.</summary>
    public bool IsKillSwitchTripped => _killSwitchTripped;

    /// <summary>Gets whether the current UTC risk day's daily loss limit has latched.</summary>
    public bool IsDailyLossLimitTripped => _dailyLossTripped;

    /// <summary>Gets immutable decisions in evaluation order; entries are never rewritten or removed.</summary>
    public IReadOnlyList<RiskDecisionRecord> Decisions => _decisionView;

    /// <summary>
    /// Replaces the policy for future evaluations. Existing decision records retain their copied
    /// policy identity, hash, limits, inputs, reasons, and exposures unchanged. The daily-loss latch
    /// is cleared so the next intent is explained solely by the replacement policy and current input;
    /// the independent manual kill switch remains tripped when already set.
    /// </summary>
    public void ReplacePolicy(RiskPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _hasDailyLossDay = false;
        _dailyLossTripped = false;
    }

    /// <summary>
    /// Irreversibly disarms new intents for this engine instance. It neither cancels working orders
    /// nor creates a flatten intent; those actions are deliberately outside this increment.
    /// </summary>
    public void TripKillSwitch() => _killSwitchTripped = true;

    /// <summary>
    /// Evaluates and records one complete intent before admission. Every rejection is a value with
    /// the attempted intent and projected exposure; no cap silently changes quantity or throws.
    /// </summary>
    public RiskDecisionRecord Evaluate(in RiskInputSnapshot input)
    {
        var policy = _policy;
        var reasons = _killSwitchTripped ? RiskReasonCode.KillSwitchTripped : RiskReasonCode.None;
        var signedOrderQuantity = ScaledQuantity.Zero;
        var orderNotional = ScaledMoney.Zero;
        var exposureBefore = new RiskExposureSnapshot(
            input.PositionBefore,
            ScaledMoney.Zero,
            input.GrossExposureBefore);
        var exposureAfter = exposureBefore;

        if (!IsStructurallyValid(input, out var currentUnits, out var intentUnits))
        {
            reasons |= RiskReasonCode.InvalidSnapshot;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }

        if (!_hasDailyLossDay || input.RiskDay != _dailyLossDay)
        {
            _dailyLossDay = input.RiskDay;
            _hasDailyLossDay = true;
            _dailyLossTripped = false;
        }

        if (_dailyLossTripped)
        {
            reasons |= RiskReasonCode.DailyLossLimitExceeded;
        }
        else if (!TryDailyLossReached(input, policy.Limits.DailyLossLimit, out var dailyLossReached))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
        }
        else if (dailyLossReached)
        {
            _dailyLossTripped = true;
            reasons |= RiskReasonCode.DailyLossLimitExceeded;
        }

        if (!TryProjectQuantities(input.Intent.QuantityMode, currentUnits, intentUnits, out var orderUnits, out var afterUnits))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }

        signedOrderQuantity = ScaledQuantity.FromWhole(orderUnits);
        var absoluteOrder = Absolute(orderUnits);
        var absoluteBefore = Absolute(currentUnits);
        var absoluteAfter = Absolute(afterUnits);
        if (!RiskMath.TryExposureMoney(absoluteOrder, input.ReferencePrice, input.ContractMultiplier, out orderNotional) ||
            !RiskMath.TryExposureMoney(absoluteBefore, input.ReferencePrice, input.ContractMultiplier, out var instrumentBefore) ||
            !RiskMath.TryExposureMoney(absoluteAfter, input.ReferencePrice, input.ContractMultiplier, out var instrumentAfter))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }

        exposureBefore = new RiskExposureSnapshot(input.PositionBefore, instrumentBefore, input.GrossExposureBefore);
        if (!RiskMath.TrySubtractMoney(input.GrossExposureBefore, instrumentBefore, out var grossWithoutInstrument))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }
        if (grossWithoutInstrument.Coefficient < 0)
        {
            reasons |= RiskReasonCode.InvalidSnapshot;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }
        if (!RiskMath.TryAddMoney(grossWithoutInstrument, instrumentAfter, out var grossAfter))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
            return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
        }

        exposureAfter = new RiskExposureSnapshot(ScaledQuantity.FromWhole(afterUnits), instrumentAfter, grossAfter);
        var limits = policy.Limits;
        _ = limits.MaximumOrderQuantity.TryGetWholeUnits(out var maximumOrderUnits);
        _ = limits.MaximumAbsolutePositionPerInstrument.TryGetWholeUnits(out var maximumPositionUnits);
        if (absoluteOrder > maximumOrderUnits)
            reasons |= RiskReasonCode.MaximumOrderQuantityExceeded;
        if (absoluteAfter > maximumPositionUnits)
            reasons |= RiskReasonCode.MaximumAbsolutePositionExceeded;

        if (!RiskMath.TryCompareNonNegative(orderNotional, limits.MaximumOrderNotional, out var orderComparison) ||
            !RiskMath.TryCompareNonNegative(grossAfter, limits.MaximumGrossExposure, out var grossComparison))
        {
            reasons |= RiskReasonCode.ArithmeticOverflow;
        }
        else
        {
            if (orderComparison > 0)
                reasons |= RiskReasonCode.MaximumOrderNotionalExceeded;
            if (grossComparison > 0)
                reasons |= RiskReasonCode.MaximumGrossExposureExceeded;
        }

        return Record(policy, input, reasons, signedOrderQuantity, orderNotional, exposureBefore, exposureAfter);
    }

    private RiskDecisionRecord Record(
        RiskPolicy policy,
        in RiskInputSnapshot input,
        RiskReasonCode reasons,
        ScaledQuantity signedOrderQuantity,
        ScaledMoney orderNotional,
        RiskExposureSnapshot exposureBefore,
        RiskExposureSnapshot exposureAfter)
    {
        var record = new RiskDecisionRecord(
            policy.PolicyId,
            policy.PolicyVersion,
            policy.PolicyHash,
            policy.Limits,
            input,
            reasons == RiskReasonCode.None ? RiskDecisionOutcome.Accepted : RiskDecisionOutcome.Rejected,
            reasons,
            signedOrderQuantity,
            orderNotional,
            exposureBefore,
            exposureAfter);
        _decisions.Add(record);
        return record;
    }

    private static bool IsStructurallyValid(
        in RiskInputSnapshot input,
        out long currentUnits,
        out long intentUnits)
    {
        currentUnits = 0;
        intentUnits = 0;
        return input.IsComplete &&
               input.RiskDay != default &&
               !input.Intent.Instrument.IsNone &&
               Enum.IsDefined(input.Intent.QuantityMode) &&
               input.PositionBefore.TryGetWholeUnits(out currentUnits) &&
               input.Intent.SignedUnits.TryGetWholeUnits(out intentUnits) &&
               input.ReferencePrice.IsValid && input.ReferencePrice.Coefficient > 0 &&
               input.ContractMultiplier.IsValid && input.ContractMultiplier.Coefficient > 0 &&
               input.GrossExposureBefore.IsValid && input.GrossExposureBefore.Coefficient >= 0 &&
               input.DailyRealizedPnl.IsValid &&
               input.DailyMarkToMarketPnl.IsValid;
    }

    private static bool TryProjectQuantities(
        TradeIntentQuantityMode mode,
        long currentUnits,
        long intentUnits,
        out long orderUnits,
        out long afterUnits)
    {
        orderUnits = 0;
        afterUnits = 0;
        var current = (Int128)currentUnits;
        var intent = (Int128)intentUnits;
        var after = mode == TradeIntentQuantityMode.TargetPosition ? intent : current + intent;
        var order = mode == TradeIntentQuantityMode.TargetPosition ? intent - current : intent;
        if (after < long.MinValue || after > long.MaxValue || order < long.MinValue || order > long.MaxValue)
            return false;
        afterUnits = (long)after;
        orderUnits = (long)order;
        return true;
    }

    private static bool TryDailyLossReached(
        in RiskInputSnapshot input,
        ScaledMoney dailyLossLimit,
        out bool reached)
    {
        reached = false;
        if (!ScaledValueMath.TryAdd(
                input.DailyRealizedPnl.Coefficient,
                input.DailyRealizedPnl.Scale,
                input.DailyMarkToMarketPnl.Coefficient,
                input.DailyMarkToMarketPnl.Scale,
                out var totalPnl,
                out var totalScale))
        {
            return false;
        }
        if (totalPnl >= 0)
            return true;
        var loss = -totalPnl;
        if (!ScaledValueMath.TryComparePositive(
                loss,
                totalScale,
                dailyLossLimit.Coefficient,
                dailyLossLimit.Scale,
                out var comparison))
        {
            return false;
        }
        reached = comparison >= 0;
        return true;
    }

    private static Int128 Absolute(long value) => value < 0 ? -(Int128)value : value;
}

internal static class RiskMath
{
    internal static bool TryExposureMoney(
        Int128 absoluteQuantity,
        ScaledPrice referencePrice,
        ScaledRatio contractMultiplier,
        out ScaledMoney exposure)
    {
        exposure = default;
        if (absoluteQuantity < 0 ||
            !referencePrice.IsValid || referencePrice.Coefficient <= 0 ||
            !contractMultiplier.IsValid || contractMultiplier.Coefficient <= 0 ||
            !ScaledValueMath.TryMultiply(
                referencePrice.Coefficient,
                contractMultiplier.Coefficient,
                out var perUnit))
        {
            return false;
        }

        var scale = referencePrice.Scale + contractMultiplier.Scale;
        ScaledValueMath.Normalize(ref perUnit, ref scale);
        if (!ScaledValueMath.TryMultiply(perUnit, absoluteQuantity, out var total) ||
            !ScaledValueMath.TryNarrow(total, scale, out var coefficient, out var narrowedScale))
        {
            return false;
        }
        exposure = new ScaledMoney(coefficient, narrowedScale);
        return true;
    }

    internal static bool TryMarkToMarketMoney(
        ScaledPrice referencePrice,
        ScaledPrice averagePrice,
        long signedQuantity,
        ScaledRatio contractMultiplier,
        out ScaledMoney markToMarket)
    {
        markToMarket = default;
        if (!referencePrice.IsValid || referencePrice.Coefficient <= 0 ||
            !averagePrice.IsValid || averagePrice.Coefficient <= 0 ||
            !contractMultiplier.IsValid || contractMultiplier.Coefficient <= 0 ||
            !ScaledValueMath.TryAdd(
                referencePrice.Coefficient,
                referencePrice.Scale,
                -(Int128)averagePrice.Coefficient,
                averagePrice.Scale,
                out var priceDifference,
                out var priceScale) ||
            !ScaledValueMath.TryMultiply(priceDifference, signedQuantity, out var positionDifference) ||
            !ScaledValueMath.TryMultiply(
                positionDifference,
                contractMultiplier.Coefficient,
                out var pnl) ||
            !ScaledValueMath.TryNarrow(
                pnl,
                priceScale + contractMultiplier.Scale,
                out var coefficient,
                out var scale))
        {
            return false;
        }
        markToMarket = new ScaledMoney(coefficient, scale);
        return true;
    }

    internal static bool TryAddMoney(ScaledMoney left, ScaledMoney right, out ScaledMoney sum) =>
        TryAddMoney(left.Coefficient, left.Scale, right.Coefficient, right.Scale, out sum);

    internal static bool TrySubtractMoney(ScaledMoney left, ScaledMoney right, out ScaledMoney difference) =>
        TryAddMoney(left.Coefficient, left.Scale, -(Int128)right.Coefficient, right.Scale, out difference);

    internal static bool TryCompareNonNegative(ScaledMoney value, ScaledMoney cap, out int comparison)
    {
        comparison = 0;
        if (!value.IsValid || value.Coefficient < 0 || !cap.IsValid || cap.Coefficient <= 0)
            return false;
        if (value.Coefficient == 0)
        {
            comparison = -1;
            return true;
        }
        return ScaledValueMath.TryComparePositive(
            value.Coefficient,
            value.Scale,
            cap.Coefficient,
            cap.Scale,
            out comparison);
    }

    private static bool TryAddMoney(
        Int128 left,
        int leftScale,
        Int128 right,
        int rightScale,
        out ScaledMoney sum)
    {
        sum = default;
        if (!ScaledValueMath.TryAdd(left, leftScale, right, rightScale, out var total, out var scale) ||
            !ScaledValueMath.TryNarrow(total, scale, out var coefficient, out var narrowedScale))
        {
            return false;
        }
        sum = new ScaledMoney(coefficient, narrowedScale);
        return true;
    }
}
