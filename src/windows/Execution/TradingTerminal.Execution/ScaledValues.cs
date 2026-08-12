namespace TradingTerminal.Execution;

/// <summary>
/// An exact signed quantity encoded as an integer coefficient and a base-10 scale. This is the
/// quantity representation required by the unified-execution ADR D7; no binary floating-point
/// value is stored.
/// </summary>
public readonly record struct ScaledQuantity(long Coefficient, byte Scale = 0)
{
    /// <summary>The exact zero quantity.</summary>
    public static ScaledQuantity Zero => new(0, 0);

    /// <summary>Creates an integral quantity without rounding.</summary>
    public static ScaledQuantity FromWhole(long units) => new(units, 0);

    /// <summary>Gets whether the coefficient/scale pair is within this policy's exact range.</summary>
    public bool IsValid => Scale <= ScaledValueMath.MaximumScale;

    /// <summary>Attempts to express this exact value as whole units.</summary>
    public bool TryGetWholeUnits(out long units) =>
        ScaledValueMath.TryGetWholeUnits(Coefficient, Scale, out units);
}

/// <summary>
/// An exact signed price encoded as an integer coefficient and a base-10 scale, per unified-
/// execution ADR D7.
/// </summary>
public readonly record struct ScaledPrice(long Coefficient, byte Scale)
{
    /// <summary>Gets whether the coefficient/scale pair is within this policy's exact range.</summary>
    public bool IsValid => Scale <= ScaledValueMath.MaximumScale;
}

/// <summary>
/// An exact signed money value encoded as an integer coefficient and a base-10 scale, per unified-
/// execution ADR D7.
/// </summary>
public readonly record struct ScaledMoney(long Coefficient, byte Scale)
{
    /// <summary>The exact zero-money value.</summary>
    public static ScaledMoney Zero => new(0, 0);

    /// <summary>Gets whether the coefficient/scale pair is within this policy's exact range.</summary>
    public bool IsValid => Scale <= ScaledValueMath.MaximumScale;
}

/// <summary>
/// An exact dimensionless ratio encoded as an integer coefficient and a base-10 scale. Contract
/// multipliers use this type so exact money-at-risk arithmetic never enters binary floating point.
/// </summary>
public readonly record struct ScaledRatio(long Coefficient, byte Scale)
{
    /// <summary>Gets whether the coefficient/scale pair is within this policy's exact range.</summary>
    public bool IsValid => Scale <= ScaledValueMath.MaximumScale;
}

internal static class ScaledValueMath
{
    internal const byte MaximumScale = 18;

    internal static bool TryGetWholeUnits(long coefficient, byte scale, out long units)
    {
        units = 0;
        if (scale > MaximumScale)
            return false;

        var divisor = Pow10(scale);
        if ((Int128)coefficient % divisor != 0)
            return false;

        var value = (Int128)coefficient / divisor;
        if (value < long.MinValue || value > long.MaxValue)
            return false;

        units = (long)value;
        return true;
    }

    internal static Int128 Pow10(int scale)
    {
        Int128 value = 1;
        for (var index = 0; index < scale; index++)
            value *= 10;
        return value;
    }

    internal static bool TryMultiplyPower10(Int128 value, int scale, out Int128 result)
    {
        result = value;
        for (var index = 0; index < scale; index++)
        {
            if (result > Int128.MaxValue / 10 || result < Int128.MinValue / 10)
            {
                result = 0;
                return false;
            }
            result *= 10;
        }
        return true;
    }

    internal static bool TryAlign(
        Int128 left,
        int leftScale,
        Int128 right,
        int rightScale,
        out Int128 alignedLeft,
        out Int128 alignedRight,
        out int scale)
    {
        Normalize(ref left, ref leftScale);
        Normalize(ref right, ref rightScale);
        alignedLeft = left;
        alignedRight = right;
        scale = Math.Max(leftScale, rightScale);
        return TryMultiplyPower10(left, scale - leftScale, out alignedLeft) &&
               TryMultiplyPower10(right, scale - rightScale, out alignedRight);
    }

    internal static bool TryMultiply(Int128 left, Int128 right, out Int128 result)
    {
        result = 0;
        if (left == 0 || right == 0)
            return true;

        if (left > 0)
        {
            if (right > 0 && left > Int128.MaxValue / right ||
                right < 0 && right < Int128.MinValue / left)
                return false;
        }
        else
        {
            if (right > 0 && left < Int128.MinValue / right ||
                right < 0 && left < Int128.MaxValue / right)
                return false;
        }

        result = left * right;
        return true;
    }

    internal static void Normalize(ref Int128 coefficient, ref int scale)
    {
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
    }

    internal static bool TryComparePositive(
        Int128 left,
        int leftScale,
        Int128 right,
        int rightScale,
        out int comparison)
    {
        comparison = 0;
        if (left <= 0 || right <= 0 || leftScale < 0 || rightScale < 0)
            return false;

        Normalize(ref left, ref leftScale);
        Normalize(ref right, ref rightScale);
        if (leftScale == rightScale)
        {
            comparison = left.CompareTo(right);
            return true;
        }

        if (leftScale > rightScale)
        {
            var divisor = Pow10(leftScale - rightScale);
            var quotient = left / divisor;
            comparison = quotient.CompareTo(right);
            if (comparison == 0 && left % divisor != 0)
                comparison = 1;
            return true;
        }

        var rightDivisor = Pow10(rightScale - leftScale);
        var rightQuotient = right / rightDivisor;
        comparison = left.CompareTo(rightQuotient);
        if (comparison == 0 && right % rightDivisor != 0)
            comparison = -1;
        return true;
    }

    internal static bool TryAdd(
        Int128 left,
        int leftScale,
        Int128 right,
        int rightScale,
        out Int128 coefficient,
        out int scale)
    {
        coefficient = 0;
        if (!TryAlign(left, leftScale, right, rightScale, out var alignedLeft, out var alignedRight, out scale))
            return false;
        if (alignedRight > 0 && alignedLeft > Int128.MaxValue - alignedRight ||
            alignedRight < 0 && alignedLeft < Int128.MinValue - alignedRight)
            return false;
        coefficient = alignedLeft + alignedRight;
        return true;
    }

    internal static bool TryRoundRatioToLong(Int128 numerator, Int128 denominator, out long value)
    {
        value = 0;
        if (denominator <= 0)
            return false;

        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        var absoluteRemainder = remainder < 0 ? -remainder : remainder;
        var doubled = absoluteRemainder * 2;
        if (doubled > denominator || doubled == denominator && (quotient & 1) != 0)
            quotient += numerator < 0 ? -1 : 1;

        if (quotient < long.MinValue || quotient > long.MaxValue)
            return false;
        value = (long)quotient;
        return true;
    }

    internal static bool TryNarrow(
        Int128 coefficient,
        int scale,
        out long narrowedCoefficient,
        out byte narrowedScale)
    {
        narrowedCoefficient = 0;
        narrowedScale = 0;
        if (scale < 0)
            return false;

        Normalize(ref coefficient, ref scale);
        if (scale > MaximumScale || coefficient < long.MinValue || coefficient > long.MaxValue)
            return false;

        narrowedCoefficient = (long)coefficient;
        narrowedScale = (byte)scale;
        return true;
    }

    internal static bool TryQuantizeDouble(double value, byte scale, out long coefficient)
    {
        coefficient = 0;
        if (!double.IsFinite(value) || scale > MaximumScale)
            return false;

        try
        {
            var factor = (decimal)Pow10(scale);
            var scaled = decimal.Round((decimal)value * factor, 0, MidpointRounding.ToEven);
            if (scaled is < long.MinValue or > long.MaxValue)
                return false;
            coefficient = (long)scaled;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

}
