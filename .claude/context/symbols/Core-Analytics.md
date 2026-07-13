# TradingTerminal.Core / Analytics — public API surface

Generated 2026-07-13. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Analytics/CorrelationCalculator.cs
```cs
   14: public static class CorrelationCalculator
   19: public static IReadOnlyList<double> LogReturns(IReadOnlyList<double> closes)
   41: public static (IReadOnlyList<DateTime> Timestamps, double[][] AlignedCloses) AlignByTimestamp(
   78: public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
  108: public static double[,] PearsonMatrix(IReadOnlyList<IReadOnlyList<double>> returnSeries)
```

## src/windows/Core/TradingTerminal.Core/Analytics/CorrelationResult.cs
```cs
   10: public sealed record CorrelationMatrix(
   15: public int Size => Labels.Count;
   17: public double At(int row, int col) => Values[row, col];
```
