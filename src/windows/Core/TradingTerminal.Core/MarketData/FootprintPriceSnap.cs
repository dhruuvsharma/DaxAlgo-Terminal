namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Price snapping for footprint buckets. Futures use a fixed tick; crypto often needs
/// decimal-place rounding (Bookmap "crypto rounding") so fractional prints land in stable rows.
/// </summary>
public static class FootprintPriceSnap
{
    /// <summary>
    /// Snaps <paramref name="price"/> into a footprint row key.
    /// When <paramref name="cryptoDecimals"/> is ≥ 0, rounds to that many decimal places first,
    /// then snaps to <paramref name="tickSize"/> (so BTC at 0.1 tick with 1 decimal still groups).
    /// Pass <c>cryptoDecimals = -1</c> to disable crypto pre-rounding (futures default).
    /// </summary>
    public static double Snap(double price, double tickSize, int cryptoDecimals = -1)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (!double.IsFinite(price)) return price;

        var p = price;
        if (cryptoDecimals >= 0)
            p = Math.Round(p, cryptoDecimals, MidpointRounding.AwayFromZero);

        return Math.Round(p / tickSize, MidpointRounding.AwayFromZero) * tickSize;
    }

    /// <summary>
    /// Bookmap-style vertical smart scaling: pick a display tick that yields roughly
    /// <paramref name="targetRows"/> rows across <paramref name="priceSpan"/>, snapped to a
    /// multiple of the instrument tick.
    /// </summary>
    public static double AutoScaleTick(double priceSpan, double instrumentTick, int targetRows = 24)
    {
        if (instrumentTick <= 0) throw new ArgumentOutOfRangeException(nameof(instrumentTick));
        if (targetRows <= 0) throw new ArgumentOutOfRangeException(nameof(targetRows));
        if (!double.IsFinite(priceSpan) || priceSpan <= 0) return instrumentTick;

        var raw = priceSpan / targetRows;
        var multiples = Math.Max(1L, (long)Math.Round(raw / instrumentTick, MidpointRounding.AwayFromZero));
        return multiples * instrumentTick;
    }
}
