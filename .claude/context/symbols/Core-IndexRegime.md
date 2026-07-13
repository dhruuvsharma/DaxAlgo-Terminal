# TradingTerminal.Core / IndexRegime — public API surface

Generated 2026-07-13. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/IndexRegime/IndexRegimeAggregator.cs
```cs
   15: public static class IndexRegimeAggregator
   21: public static IndexRegimeSnapshot Aggregate(
  107: public static CellSignal BandFor(double score) => score switch
```

## src/windows/Core/TradingTerminal.Core/IndexRegime/IndexRegimeModels.cs
```cs
    8: public readonly record struct TimeframeScore(string Label, double Score, int TrendScore);
   17: public sealed record ConstituentRegimeScore(
   35: public sealed record IndexRegimeSnapshot(
   47: public static IndexRegimeSnapshot Empty { get; } = new(
```

## src/windows/Core/TradingTerminal.Core/IndexRegime/RegimeHorizon.cs
```cs
    9: public enum RegimeHorizon
   30: public static class TimeframeWeighting
   33: public static string Describe(RegimeHorizon horizon) => horizon switch
   45: public static IReadOnlyDictionary<string, double> For(RegimeHorizon horizon) => horizon switch
   71: public const double FloorWeight = 0.01;
```
