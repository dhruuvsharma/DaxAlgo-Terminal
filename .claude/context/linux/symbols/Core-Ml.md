# TradingTerminal.Core / Ml — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Ml/FactorComputation.cs
```cs
   14: public static class FactorComputation
   18: public sealed record FeatureBar(
   27: public sealed record CorrelationMatrix(
   31: public sealed record DecileSortResult(
   36: public sealed record DecileRow(int Decile, int Count, double LowerEdge, double UpperEdge, double MeanForwardReturn);
   40: public static IReadOnlyList<FeatureBar> ComputeBars(IReadOnlyList<Tick> ticks, int barTicks = 100, int volWindow = 20)
  105: public static CorrelationMatrix Correlations(IReadOnlyList<FeatureBar> bars)
  149: public static DecileSortResult DecileSort(
```

## src/linux/Core/TradingTerminal.Core/Ml/OnlineLinearRegression.cs
```cs
   18: public sealed class OnlineLinearRegression
   26: public OnlineLinearRegression(int dimensions, double lambda = 0.99, double initialDiagonal = 1e3)
   38: public int Dimensions => _d;
   39: public double Lambda { get; }
   40: public long Samples => _samples;
   41: public IReadOnlyList<double> Coefficients => _beta;
   44: public double Predict(IReadOnlyList<double> features)
   53: public void Update(IReadOnlyList<double> features, double y)
```

## src/linux/Core/TradingTerminal.Core/Ml/TripleBarrierLabeler.cs
```cs
   20: public static class TripleBarrierLabeler
   22: public enum Label { Negative = -1, Neutral = 0, Positive = 1 }
   24: public sealed record LabelledBar<TBar>(int Index, TBar Bar, Label Label, int BarsToOutcome);
   33: public static IReadOnlyList<LabelledBar<TBar>> Apply<TBar>(
```
