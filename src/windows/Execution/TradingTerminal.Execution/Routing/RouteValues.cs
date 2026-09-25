using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Routing;

/// <summary>
/// Exact conversions between a route's decimal numbers and the engine's scaled integers. Nothing here
/// rounds except where a method says so; a value that does not fit comes back as a refusal.
/// </summary>
internal static class RouteValues
{
    /// <summary>A positive decimal as an exact <see cref="ScaledPrice"/>.</summary>
    public static bool TryPrice(decimal value, out ScaledPrice price)
    {
        price = default;
        if (value <= 0 || !TrySplit(value, out var coefficient, out var scale))
            return false;
        price = new ScaledPrice(coefficient, scale);
        return true;
    }

    /// <summary>Any decimal as an exact <see cref="ScaledMoney"/>.</summary>
    public static bool TryMoney(decimal value, out ScaledMoney money)
    {
        money = default;
        if (!TrySplit(value, out var coefficient, out var scale))
            return false;
        money = new ScaledMoney(coefficient, scale);
        return true;
    }

    public static bool TryRatio(decimal value, out ScaledRatio ratio)
    {
        ratio = default;
        if (value <= 0 || !TrySplit(value, out var coefficient, out var scale))
            return false;
        ratio = new ScaledRatio(coefficient, scale);
        return true;
    }

    public static bool TryQuantity(decimal value, out ScaledQuantity quantity)
    {
        quantity = default;
        if (!TrySplit(value, out var coefficient, out var scale))
            return false;
        quantity = new ScaledQuantity(coefficient, scale);
        return true;
    }

    public static decimal? ToDecimal(ScaledPrice? price) =>
        price is { } value ? value.Coefficient / Pow10(value.Scale) : null;

    /// <summary><paramref name="native"/> as a whole number of units of <paramref name="unitSize"/>, or false
    /// when it is not an exact multiple.</summary>
    public static bool TryUnitsExact(decimal native, decimal unitSize, out long units)
    {
        units = 0;
        if (unitSize <= 0)
            return false;
        var quotient = native / unitSize;
        if (quotient != decimal.Truncate(quotient) || quotient > long.MaxValue || quotient < long.MinValue)
            return false;
        units = (long)quotient;
        return true;
    }

    public static long Units(ScaledQuantity quantity) => quantity.TryGetWholeUnits(out var units) ? units : long.MaxValue;

    /// <summary>
    /// The price of the latest fill from a broker that reports only a cumulative average:
    /// (average × filled − previous average × previously filled) ÷ newly filled. Rounded to twelve decimal
    /// places, because the quotient of two exact averages need not terminate; the error is below a
    /// hundred-millionth of a tick on any instrument traded here.
    /// </summary>
    public static bool TryIncrementalPrice(decimal priorFilled, decimal? priorAverage, decimal filled, decimal average, out decimal price)
    {
        price = 0;
        var delta = filled - priorFilled;
        if (delta <= 0 || average <= 0)
            return false;
        if (priorFilled == 0)
        {
            // Brokers that report cost and quantity leave the average to be divided out, and it need not
            // terminate either.
            price = decimal.Round(average, 12);
            return price > 0;
        }
        if (priorAverage is not { } prior || prior <= 0)
            return false;
        try
        {
            price = decimal.Round((average * filled - prior * priorFilled) / delta, 12);
        }
        catch (OverflowException)
        {
            return false;
        }
        return price > 0;
    }

    private static bool TrySplit(decimal value, out long coefficient, out byte scale)
    {
        coefficient = 0;
        scale = 0;
        // Drop trailing zeros so 1.500 and 1.5 are the same exact value with the smallest scale.
        var normal = value / 1.000000000000000000000000000000000m;
        var digits = normal.Scale;
        if (digits > ScaledValueMath.MaximumScale)
        {
            normal = decimal.Round(normal, ScaledValueMath.MaximumScale);
            if (normal != value / 1.000000000000000000000000000000000m)
                return false;
            digits = normal.Scale;
        }
        try
        {
            var raw = decimal.Truncate(normal * Pow10((byte)digits));
            if (raw > long.MaxValue || raw < long.MinValue)
                return false;
            coefficient = (long)raw;
            scale = (byte)digits;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal Pow10(byte scale)
    {
        var result = 1m;
        for (var i = 0; i < scale; i++)
            result *= 10m;
        return result;
    }
}
