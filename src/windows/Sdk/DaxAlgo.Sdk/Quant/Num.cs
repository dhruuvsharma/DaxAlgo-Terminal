namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// The arithmetic guards every estimator in this namespace uses, exposed because authored code needs
/// them too.
///
/// <para>These are three lines each and would be unremarkable, except that the mistakes they prevent
/// are the ones that survive every check the host makes. A division by a spread that momentarily
/// closed produces an infinity, which propagates silently through the rest of a frame and reaches
/// <c>IRenderSurface</c> as a non-finite coordinate; a <c>NaN</c> compares false against every
/// threshold, so a strategy holding one simply stops trading and never says why.</para>
///
/// <para>The renderer already refuses non-finite coordinates, so a picture built on one is blank
/// rather than wrong. Nothing does the equivalent for a trading decision.</para>
/// </summary>
public static class Num
{
    /// <summary>The smallest denominator <see cref="SafeDiv"/> will divide by. Chosen well below any
    /// price, size or volatility a real instrument produces, so it only ever intercepts a genuine
    /// zero.</summary>
    public const double Epsilon = 1e-12d;

    /// <summary>
    /// <paramref name="numerator"/> ÷ <paramref name="denominator"/>, returning
    /// <paramref name="fallback"/> when the denominator is zero, sub-epsilon, or not finite.
    /// </summary>
    public static double SafeDiv(double numerator, double denominator, double fallback = 0d) =>
        !double.IsFinite(numerator) || !double.IsFinite(denominator) || Math.Abs(denominator) < Epsilon
            ? fallback
            : numerator / denominator;

    /// <summary><paramref name="value"/> when it is finite, else <paramref name="fallback"/>. The last
    /// guard before a number reaches a decision or a draw call.</summary>
    public static double Finite(double value, double fallback = 0d) =>
        double.IsFinite(value) ? value : fallback;

    /// <summary>Constrains a value to a range. Bounds given the wrong way round are swapped rather
    /// than producing the empty interval <see cref="Math.Clamp(double,double,double)"/> throws on.</summary>
    public static double Clamp(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value)) return minimum;
        if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

    /// <summary>How many standard deviations <paramref name="value"/> sits from the mean, with the
    /// denominator floored. Zero when the sample has no dispersion — which is the honest answer, not
    /// an infinite one.</summary>
    public static double ZScore(double value, double mean, double standardDeviation) =>
        SafeDiv(value - mean, standardDeviation);

    /// <summary>
    /// The log return between two prices, or zero when either is non-positive.
    ///
    /// <para>Log returns rather than percentage returns because they add across time, so a sum over a
    /// window is the window's return, and because they are symmetric: a move down and the move that
    /// undoes it are equal and opposite, which percentage returns are not.</para>
    /// </summary>
    public static double LogReturn(double price, double previousPrice) =>
        price > 0d && previousPrice > 0d ? Math.Log(price / previousPrice) : 0d;

    /// <summary>Linear interpolation, with <paramref name="t"/> clamped to [0, 1].</summary>
    public static double Lerp(double from, double to, double t) =>
        from + ((to - from) * Clamp(t, 0d, 1d));

    /// <summary>
    /// <paramref name="price"/> snapped to the nearest multiple of <paramref name="tickSize"/>.
    ///
    /// <para>A price that is not on the instrument's grid is rejected by the venue, so a level
    /// computed from a moving average has to be rounded before it can be a stop or a target. A
    /// non-positive tick size returns the price unchanged rather than throwing — an instrument whose
    /// tick is unknown is common, and losing the level would be worse than not snapping it.</para>
    /// </summary>
    public static double RoundToTick(double price, double tickSize) =>
        tickSize > 0d && double.IsFinite(price) ? Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize : price;
}
