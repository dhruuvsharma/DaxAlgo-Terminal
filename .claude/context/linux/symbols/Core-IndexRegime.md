# TradingTerminal.Core / IndexRegime — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/IndexRegime/IndexRegimeAggregator.cs
```cs
   15: public static class IndexRegimeAggregator
   21: public static IndexRegimeSnapshot Aggregate(
  107: public static CellSignal BandFor(double score) => score switch
```

## src/linux/Core/TradingTerminal.Core/IndexRegime/IndexRegimeModels.cs
```cs
    8: public readonly record struct TimeframeScore(string Label, double Score, int TrendScore);
   17: public sealed record ConstituentRegimeScore(
   35: public sealed record IndexRegimeSnapshot(
   47: public static IndexRegimeSnapshot Empty { get; } = new(
```

## src/linux/Core/TradingTerminal.Core/IndexRegime/RegimeHorizon.cs
```cs
    9: public enum RegimeHorizon
   30: public static class TimeframeWeighting
   33: public static string Describe(RegimeHorizon horizon) => horizon switch
   45: public static IReadOnlyDictionary<string, double> For(RegimeHorizon horizon) => horizon switch
   71: public const double FloorWeight = 0.01;
```
