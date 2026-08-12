using System.Runtime.CompilerServices;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Execution;

/// <summary>Observable, deterministic outcomes from policy construction or signal evaluation.</summary>
public enum SignalExecutionFault : byte
{
    /// <summary>The policy operation succeeded.</summary>
    None = 0,

    /// <summary>The constructor-supplied policy version is missing or invalid.</summary>
    InvalidPolicyVersion = 1,

    /// <summary>The immutable cost, cap, or protective-exit configuration is invalid.</summary>
    InvalidPolicyOptions = 2,

    /// <summary>The public strategy signal is outside its documented domain.</summary>
    InvalidSignal = 3,

    /// <summary>The stable strategy provenance is missing or invalid.</summary>
    InvalidStrategyProvenance = 4,

    /// <summary>An exact market/account input is invalid or cannot be represented.</summary>
    InvalidInputs = 5,

    /// <summary>The selected buyer unit definition is invalid.</summary>
    InvalidUnitDefinition = 6,

    /// <summary>Risk sizing or a cash-risk cap needs an exact risk distance that was not supplied.</summary>
    MissingSizingRisk = 7,

    /// <summary>An exact integer intermediate exceeded the supported coefficient/scale range.</summary>
    ArithmeticOverflow = 8,

    /// <summary>The candidate target exceeds the buyer's absolute-unit ceiling.</summary>
    BuyerUnitCapExceeded = 9,

    /// <summary>The candidate target exceeds the buyer's exact notional ceiling.</summary>
    BuyerNotionalCapExceeded = 10,

    /// <summary>The candidate target exceeds the buyer's exact cash-risk ceiling.</summary>
    BuyerCashRiskCapExceeded = 11,
}

/// <summary>
/// A fault-as-value result from <see cref="SignalExecutionPolicy.Evaluate"/>. Rejections expose the
/// complete attempted target and never contain a silently clamped intent.
/// </summary>
public readonly record struct SignalExecutionDecision(
    SignalExecutionFault Fault,
    TradeIntent? Intent,
    ScaledQuantity CandidateTargetUnits)
{
    /// <summary>Gets whether a complete intent was accepted.</summary>
    public bool IsAccepted => Fault == SignalExecutionFault.None && Intent.HasValue;
}

/// <summary>
/// Versioned, deterministic host policy implementing unified-execution ADR D4. The policy is pure
/// after construction: it performs no I/O, observes no clock or ambient state, uses no randomness,
/// and carries all quantity/price/money arithmetic as exact signed coefficients plus decimal scales.
/// </summary>
public sealed class SignalExecutionPolicy
{
    private const long StrengthScale = 1_000_000;
    private readonly SignalExecutionPolicyOptions _options;
    private readonly ScaledMoney _roundTripCostPerUnit;

    private SignalExecutionPolicy(
        string policyVersion,
        SignalExecutionPolicyOptions options,
        ScaledMoney roundTripCostPerUnit)
    {
        PolicyVersion = policyVersion;
        _options = options;
        _roundTripCostPerUnit = roundTripCostPerUnit;
    }

    /// <summary>The immutable version copied to every accepted intent.</summary>
    public string PolicyVersion { get; }

    /// <summary>
    /// Validates immutable policy configuration once, following the fault ordering documented in
    /// <c>DECISIONS.md</c>, and creates a policy without hidden normalization.
    /// </summary>
    public static SignalExecutionFault TryCreate(
        string? policyVersion,
        SignalExecutionPolicyOptions options,
        out SignalExecutionPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(policyVersion) || policyVersion.Length > 128)
            return SignalExecutionFault.InvalidPolicyVersion;

        if (!TryValidateCaps(options.Caps) ||
            options.ProfitTargetMultipleBasisPoints < 0 ||
            !TrySumCosts(options.Costs, out var totalCost))
        {
            return SignalExecutionFault.InvalidPolicyOptions;
        }

        policy = new SignalExecutionPolicy(policyVersion, options, totalCost);
        return SignalExecutionFault.None;
    }

    /// <summary>
    /// Converts one ordered <see cref="StrategySignal"/> and the buyer's unit definition into a
    /// target-position intent. Evaluation and cap precedence are fixed in <c>DECISIONS.md</c>;
    /// oversized candidates are rejected observably and are never clamped.
    /// </summary>
    public SignalExecutionDecision Evaluate(
        string? strategyId,
        in StrategySignal signal,
        in UnitDefinition unitDefinition,
        in SignalExecutionInputs inputs)
    {
        if (!Enum.IsDefined(signal.Kind) || !double.IsFinite(signal.Strength) ||
            signal.Strength is < 0d or > 1d || signal.NoteId < 0)
        {
            return Reject(SignalExecutionFault.InvalidSignal, 0);
        }

        if (string.IsNullOrWhiteSpace(strategyId) || strategyId.Length > 256)
            return Reject(SignalExecutionFault.InvalidStrategyProvenance, 0);

        if (inputs.Instrument.IsNone ||
            !inputs.CurrentPosition.IsValid ||
            !inputs.CurrentPosition.TryGetWholeUnits(out _))
        {
            return Reject(SignalExecutionFault.InvalidInputs, 0);
        }

        // A flat signal is an exit instruction and must not be blocked by stale entry-sizing data.
        if (signal.Kind == StrategySignalKind.Flat || signal.Strength == 0d)
            return Accept(strategyId, signal.NoteId, inputs.Instrument, 0, null, null);

        if (!IsPositive(inputs.ReferencePrice.Coefficient, inputs.ReferencePrice.Scale) ||
            !IsPositive(inputs.Equity.Coefficient, inputs.Equity.Scale) ||
            !IsPositive(inputs.ContractMultiplier.Coefficient, inputs.ContractMultiplier.Scale))
        {
            return Reject(SignalExecutionFault.InvalidInputs, 0);
        }

        var sizingFault = TryResolveSizing(
            unitDefinition,
            inputs,
            out var sizing,
            out var sizingRiskDistance,
            out var riskPerUnit);
        if (sizingFault != SignalExecutionFault.None)
            return Reject(sizingFault, 0);

        if (!TryQuantizeStrength(signal.Strength, out var strength))
            return Reject(SignalExecutionFault.ArithmeticOverflow, 0);
        if (strength == 0)
            return Accept(strategyId, signal.NoteId, inputs.Instrument, 0, null, null);

        var sizingNumerator = sizing.Numerator;
        var sizingDenominator = sizing.Denominator;
        Int128 strengthNumerator = strength;
        Int128 strengthDenominator = StrengthScale;
        ReduceFraction(ref sizingNumerator, ref sizingDenominator);
        ReduceFraction(ref strengthNumerator, ref strengthDenominator);
        CrossReduce(ref sizingNumerator, ref strengthDenominator);
        CrossReduce(ref strengthNumerator, ref sizingDenominator);
        if (!ScaledValueMath.TryMultiply(sizingNumerator, strengthNumerator, out var weightedUnits) ||
            !ScaledValueMath.TryMultiply(sizingDenominator, strengthDenominator, out var weightedDenominator))
            return Reject(SignalExecutionFault.ArithmeticOverflow, 0);

        var absoluteTargetWide = weightedUnits / weightedDenominator;
        if (absoluteTargetWide < 0 || absoluteTargetWide > long.MaxValue)
            return Reject(SignalExecutionFault.ArithmeticOverflow, 0);

        var absoluteTarget = (long)absoluteTargetWide;
        var signedTarget = signal.Kind == StrategySignalKind.Short ? -absoluteTarget : absoluteTarget;
        if (signedTarget == 0)
            return Accept(strategyId, signal.NoteId, inputs.Instrument, 0, null, null);

        if (!_options.Caps.MaximumAbsoluteUnits.TryGetWholeUnits(out var maximumUnits) ||
            maximumUnits <= 0)
        {
            return Reject(SignalExecutionFault.InvalidPolicyOptions, signedTarget);
        }
        if (absoluteTarget > maximumUnits)
            return Reject(SignalExecutionFault.BuyerUnitCapExceeded, signedTarget);

        if (_options.Caps.MaximumNotional is { } notionalCap)
        {
            if (!TryNotional(absoluteTarget, inputs.ReferencePrice, inputs.ContractMultiplier, out var notional) ||
                !TryExceeds(notional, notionalCap, out var exceedsNotional))
            {
                return Reject(SignalExecutionFault.ArithmeticOverflow, signedTarget);
            }
            if (exceedsNotional)
                return Reject(SignalExecutionFault.BuyerNotionalCapExceeded, signedTarget);
        }

        if (_options.Caps.MaximumCashRisk is { } cashRiskCap)
        {
            if (sizingRiskDistance is null)
                return Reject(SignalExecutionFault.MissingSizingRisk, signedTarget);
            if (!ScaledValueMath.TryMultiply(riskPerUnit.Coefficient, absoluteTarget, out var totalRisk) ||
                !TryExceeds(new WideValue(totalRisk, riskPerUnit.Scale), cashRiskCap, out var exceedsRisk))
            {
                return Reject(SignalExecutionFault.ArithmeticOverflow, signedTarget);
            }
            if (exceedsRisk)
                return Reject(SignalExecutionFault.BuyerCashRiskCapExceeded, signedTarget);
        }

        ScaledPrice? stopPrice = null;
        ScaledPrice? targetPrice = null;
        if (_options.AttachSizingRiskAsProtectiveStop)
        {
            if (sizingRiskDistance is not { } riskDistance)
                return Reject(SignalExecutionFault.MissingSizingRisk, signedTarget);
            if (!TryProtectivePrice(inputs.ReferencePrice, riskDistance, signedTarget, isTarget: false, out var stop))
                return Reject(SignalExecutionFault.ArithmeticOverflow, signedTarget);
            stopPrice = stop;

            if (_options.ProfitTargetMultipleBasisPoints > 0)
            {
                if (!TryMultiplyBasisPoints(riskDistance, _options.ProfitTargetMultipleBasisPoints, out var targetDistance) ||
                    !TryProtectivePrice(inputs.ReferencePrice, targetDistance, signedTarget, isTarget: true, out var target))
                {
                    return Reject(SignalExecutionFault.ArithmeticOverflow, signedTarget);
                }
                targetPrice = target;
            }
        }

        return Accept(strategyId, signal.NoteId, inputs.Instrument, signedTarget, stopPrice, targetPrice);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SignalExecutionDecision Accept(
        string strategyId,
        long noteId,
        TradingTerminal.Core.Domain.InstrumentId instrument,
        long signedTarget,
        ScaledPrice? stop,
        ScaledPrice? target)
    {
        var quantity = ScaledQuantity.FromWhole(signedTarget);
        var intent = new TradeIntent(
            instrument,
            TradeIntentQuantityMode.TargetPosition,
            quantity,
            stop,
            target,
            _roundTripCostPerUnit,
            strategyId,
            noteId,
            PolicyVersion);
        return new SignalExecutionDecision(SignalExecutionFault.None, intent, quantity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SignalExecutionDecision Reject(SignalExecutionFault fault, long signedTarget) =>
        new(fault, null, ScaledQuantity.FromWhole(signedTarget));

    private SignalExecutionFault TryResolveSizing(
        in UnitDefinition unit,
        in SignalExecutionInputs inputs,
        out SizingRatio sizing,
        out ScaledPrice? sizingRiskDistance,
        out WideValue riskPerUnit)
    {
        sizing = default;
        sizingRiskDistance = null;
        riskPerUnit = default;

        switch (unit.Kind)
        {
            case UnitDefinitionKind.FixedContracts:
                if (unit.FixedContractCount <= 0)
                    return SignalExecutionFault.InvalidUnitDefinition;
                sizing = new SizingRatio(unit.FixedContractCount, 1);
                riskPerUnit = new WideValue(_roundTripCostPerUnit.Coefficient, _roundTripCostPerUnit.Scale);
                return SignalExecutionFault.None;

            case UnitDefinitionKind.PercentOfEquityAtRisk:
                if (unit.EquityRiskBasisPoints <= 0 ||
                    !IsPositive(unit.SizingRiskDistance.Coefficient, unit.SizingRiskDistance.Scale))
                {
                    return SignalExecutionFault.InvalidUnitDefinition;
                }
                sizingRiskDistance = unit.SizingRiskDistance;
                if (!TryRiskPerUnit(unit.SizingRiskDistance, inputs.ContractMultiplier, out riskPerUnit))
                    return SignalExecutionFault.ArithmeticOverflow;
                if (!ScaledValueMath.TryMultiply(inputs.Equity.Coefficient, unit.EquityRiskBasisPoints, out var equityBudget))
                    return SignalExecutionFault.ArithmeticOverflow;
                return TryCreateSizingRatio(
                    new WideValue(equityBudget, inputs.Equity.Scale + 4),
                    riskPerUnit,
                    out sizing);

            case UnitDefinitionKind.FixedCashRisk:
                if (!IsPositive(unit.CashRisk.Coefficient, unit.CashRisk.Scale) ||
                    !IsPositive(unit.SizingRiskDistance.Coefficient, unit.SizingRiskDistance.Scale))
                {
                    return SignalExecutionFault.InvalidUnitDefinition;
                }
                sizingRiskDistance = unit.SizingRiskDistance;
                if (!TryRiskPerUnit(unit.SizingRiskDistance, inputs.ContractMultiplier, out riskPerUnit))
                    return SignalExecutionFault.ArithmeticOverflow;
                return TryCreateSizingRatio(
                    new WideValue(unit.CashRisk.Coefficient, unit.CashRisk.Scale),
                    riskPerUnit,
                    out sizing);

            case UnitDefinitionKind.VolatilityScaled:
                if (!IsPositive(unit.CashRisk.Coefficient, unit.CashRisk.Scale) ||
                    !IsPositive(unit.Volatility.Coefficient, unit.Volatility.Scale) ||
                    unit.VolatilityMultipleBasisPoints <= 0)
                {
                    return SignalExecutionFault.InvalidUnitDefinition;
                }
                if (!TryMultiplyBasisPoints(unit.Volatility, unit.VolatilityMultipleBasisPoints, out var volatilityRisk))
                    return SignalExecutionFault.ArithmeticOverflow;
                sizingRiskDistance = volatilityRisk;
                if (!TryRiskPerUnit(volatilityRisk, inputs.ContractMultiplier, out riskPerUnit))
                    return SignalExecutionFault.ArithmeticOverflow;
                return TryCreateSizingRatio(
                    new WideValue(unit.CashRisk.Coefficient, unit.CashRisk.Scale),
                    riskPerUnit,
                    out sizing);

            default:
                return SignalExecutionFault.InvalidUnitDefinition;
        }
    }

    private bool TryRiskPerUnit(ScaledPrice distance, ScaledRatio multiplier, out WideValue risk)
    {
        risk = default;
        if (!ScaledValueMath.TryMultiply(distance.Coefficient, multiplier.Coefficient, out var priceRisk) ||
            !ScaledValueMath.TryAdd(
                priceRisk,
                distance.Scale + multiplier.Scale,
                _roundTripCostPerUnit.Coefficient,
                _roundTripCostPerUnit.Scale,
                out var coefficient,
                out var scale) || coefficient <= 0)
        {
            return false;
        }
        risk = new WideValue(coefficient, scale);
        return true;
    }

    private static SignalExecutionFault TryCreateSizingRatio(
        WideValue budget,
        WideValue perUnitRisk,
        out SizingRatio sizing)
    {
        sizing = default;
        if (budget.Coefficient <= 0 || perUnitRisk.Coefficient <= 0)
            return SignalExecutionFault.InvalidUnitDefinition;
        if (!ScaledValueMath.TryAlign(
            budget.Coefficient,
            budget.Scale,
            perUnitRisk.Coefficient,
            perUnitRisk.Scale,
            out var alignedBudget,
            out var alignedRisk,
            out _))
        {
            return SignalExecutionFault.ArithmeticOverflow;
        }
        sizing = new SizingRatio(alignedBudget, alignedRisk);
        return SignalExecutionFault.None;
    }

    private static bool TryNotional(
        long absoluteTarget,
        ScaledPrice referencePrice,
        ScaledRatio multiplier,
        out WideValue notional)
    {
        notional = default;
        if (!ScaledValueMath.TryMultiply(referencePrice.Coefficient, multiplier.Coefficient, out var oneUnit))
            return false;
        var scale = referencePrice.Scale + multiplier.Scale;
        ScaledValueMath.Normalize(ref oneUnit, ref scale);
        if (!ScaledValueMath.TryMultiply(oneUnit, absoluteTarget, out var total))
            return false;
        notional = new WideValue(total, scale);
        return true;
    }

    private static bool TryExceeds(WideValue value, ScaledMoney cap, out bool exceeds)
    {
        exceeds = false;
        if (!IsPositive(cap.Coefficient, cap.Scale) ||
            !ScaledValueMath.TryComparePositive(
                value.Coefficient,
                value.Scale,
                cap.Coefficient,
                cap.Scale,
                out var comparison))
        {
            return false;
        }
        exceeds = comparison > 0;
        return true;
    }

    private static bool TryProtectivePrice(
        ScaledPrice referencePrice,
        ScaledPrice distance,
        long signedTarget,
        bool isTarget,
        out ScaledPrice result)
    {
        result = default;
        if (!ScaledValueMath.TryAlign(
            referencePrice.Coefficient,
            referencePrice.Scale,
            distance.Coefficient,
            distance.Scale,
            out var reference,
            out var offset,
            out var scale))
        {
            return false;
        }

        var add = signedTarget > 0 == isTarget;
        if (!add)
            offset = -offset;
        if (!ScaledValueMath.TryAdd(reference, scale, offset, scale, out var price, out _) ||
            price <= 0 ||
            !ScaledValueMath.TryNarrow(price, scale, out var coefficient, out var narrowedScale))
        {
            return false;
        }
        result = new ScaledPrice(coefficient, narrowedScale);
        return true;
    }

    private static bool TryMultiplyBasisPoints(ScaledPrice value, int basisPoints, out ScaledPrice result)
    {
        result = default;
        if (basisPoints <= 0 ||
            !ScaledValueMath.TryMultiply(value.Coefficient, basisPoints, out var coefficient) ||
            coefficient <= 0)
        {
            return false;
        }

        if (!ScaledValueMath.TryNarrow(
            coefficient,
            value.Scale + 4,
            out var narrowedCoefficient,
            out var narrowedScale))
            return false;

        result = new ScaledPrice(narrowedCoefficient, narrowedScale);
        return true;
    }

    private static bool TryQuantizeStrength(double strength, out long coefficient)
    {
        coefficient = 0;
        var scaled = Math.Round(strength * StrengthScale, MidpointRounding.ToEven);
        if (!double.IsFinite(scaled) || scaled is < 0 or > StrengthScale)
            return false;
        coefficient = (long)scaled;
        return true;
    }

    private static void ReduceFraction(ref Int128 numerator, ref Int128 denominator)
    {
        var divisor = GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
    }

    private static void CrossReduce(ref Int128 leftNumerator, ref Int128 rightDenominator)
    {
        var divisor = GreatestCommonDivisor(leftNumerator, rightDenominator);
        leftNumerator /= divisor;
        rightDenominator /= divisor;
    }

    private static Int128 GreatestCommonDivisor(Int128 left, Int128 right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }

    private static bool TryValidateCaps(BuyerExecutionCaps caps)
    {
        if (!caps.MaximumAbsoluteUnits.TryGetWholeUnits(out var maximumUnits) || maximumUnits <= 0)
            return false;
        if (caps.MaximumNotional is { } notional && !IsPositive(notional.Coefficient, notional.Scale))
            return false;
        if (caps.MaximumCashRisk is { } risk && !IsPositive(risk.Coefficient, risk.Scale))
            return false;
        return true;
    }

    private static bool TrySumCosts(SignalCostAssumptions costs, out ScaledMoney total)
    {
        total = default;
        if (!IsNonNegative(costs.EntrySlippagePerUnit.Coefficient, costs.EntrySlippagePerUnit.Scale) ||
            !IsNonNegative(costs.ExitSlippagePerUnit.Coefficient, costs.ExitSlippagePerUnit.Scale) ||
            !IsNonNegative(costs.FeesPerRoundTripUnit.Coefficient, costs.FeesPerRoundTripUnit.Scale) ||
            !ScaledValueMath.TryAdd(
                costs.EntrySlippagePerUnit.Coefficient,
                costs.EntrySlippagePerUnit.Scale,
                costs.ExitSlippagePerUnit.Coefficient,
                costs.ExitSlippagePerUnit.Scale,
                out var firstTwo,
                out var firstScale) ||
            !ScaledValueMath.TryAdd(
                firstTwo,
                firstScale,
                costs.FeesPerRoundTripUnit.Coefficient,
                costs.FeesPerRoundTripUnit.Scale,
                out var all,
                out var scale) ||
            !ScaledValueMath.TryNarrow(all, scale, out var coefficient, out var narrowedScale))
        {
            return false;
        }
        total = new ScaledMoney(coefficient, narrowedScale);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPositive(long coefficient, byte scale) =>
        coefficient > 0 && scale <= ScaledValueMath.MaximumScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNonNegative(long coefficient, byte scale) =>
        coefficient >= 0 && scale <= ScaledValueMath.MaximumScale;

    private readonly record struct WideValue(Int128 Coefficient, int Scale);
    private readonly record struct SizingRatio(Int128 Numerator, Int128 Denominator);
}
