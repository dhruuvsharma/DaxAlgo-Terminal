namespace TradingTerminal.Core.MarketData;

/// <summary>Builds the Bookmap-style footprint bucketer for a <see cref="FootprintBarKind"/>.</summary>
public static class FootprintBucketerFactory
{
    /// <param name="kind">Bar partition mode.</param>
    /// <param name="tickSize">Instrument tick size (&gt; 0).</param>
    /// <param name="quality">Feed quality tag on sealed bars.</param>
    /// <param name="timeSpan">Required when <paramref name="kind"/> is <see cref="FootprintBarKind.Time"/>.</param>
    /// <param name="threshold">
    /// Reversal/Range: ticks to seal. Volume: traded size to seal. Ignored for Time.
    /// </param>
    public static IFootprintBucketer Create(
        FootprintBarKind kind,
        double tickSize,
        FeedQuality quality,
        TimeSpan timeSpan = default,
        long threshold = 0,
        int cryptoDecimals = -1)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        var options = new FootprintExtractorOptions(ImbalanceRatio: 3.0, CryptoDecimals: cryptoDecimals);

        return kind switch
        {
            FootprintBarKind.Time => new FootprintTimeBucketer(
                timeSpan > TimeSpan.Zero ? timeSpan : TimeSpan.FromMinutes(1),
                tickSize,
                quality,
                options),
            FootprintBarKind.Reversal => new FootprintReversalBucketer(
                threshold > 0 ? threshold : 4,
                tickSize,
                quality,
                options),
            FootprintBarKind.Range => new FootprintRangeBucketer(
                threshold > 0 ? threshold : 10,
                tickSize,
                quality,
                options),
            FootprintBarKind.Volume => new FootprintVolumeBucketer(
                threshold > 0 ? threshold : 1_000,
                tickSize,
                quality,
                options),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
