using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.Rsi;

/// <summary>
/// Wilder's RSI. Output array is the same length as the input bar list;
/// the first <c>period</c> entries are <see cref="double.NaN"/> (no value yet).
/// </summary>
internal static class RsiCalculator
{
    public const int DefaultPeriod = 14;
    public const double DefaultOverbought = 70d;
    public const double DefaultOversold = 30d;

    public static double[] Compute(IReadOnlyList<Bar> bars, int period = DefaultPeriod)
    {
        var rsi = new double[bars.Count];
        if (bars.Count == 0) return rsi;

        for (var i = 0; i < rsi.Length; i++) rsi[i] = double.NaN;
        if (bars.Count <= period) return rsi;

        double gainSum = 0, lossSum = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            if (change >= 0) gainSum += change;
            else             lossSum -= change;
        }

        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        rsi[period] = ComputeIndex(avgGain, avgLoss);

        for (var i = period + 1; i < bars.Count; i++)
        {
            var change = bars[i].Close - bars[i - 1].Close;
            var gain = change >= 0 ? change : 0;
            var loss = change < 0 ? -change : 0;

            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;
            rsi[i] = ComputeIndex(avgGain, avgLoss);
        }

        return rsi;
    }

    private static double ComputeIndex(double avgGain, double avgLoss)
    {
        if (avgLoss == 0) return 100d;
        var rs = avgGain / avgLoss;
        return 100d - (100d / (1d + rs));
    }
}
