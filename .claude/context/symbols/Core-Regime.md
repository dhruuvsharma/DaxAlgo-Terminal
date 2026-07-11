# TradingTerminal.Core / Regime — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Regime/IMarketRegimeProvider.cs
```cs
    9: public interface IMarketRegimeProvider
   13:     MarketRegimeSnapshot Current { get; }
   17:     IObservable<MarketRegimeSnapshot> Updates { get; }
   21:     Task<MarketRegimeSnapshot> RefreshAsync(CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/IInstrumentRegimeProvider.cs
```cs
   13: public interface IInstrumentRegimeProvider
   18:     Task<InstrumentRegimeSnapshot> AnalyseAsync(
   19:     Contract contract,
   20:     BrokerKind broker,
   21:     string displaySymbol,
   22:     BarSize timeframe,
   23:     int barCount,
   24:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeBand.cs
```cs
    8: public enum InstrumentRegimeBand
   17: public static class InstrumentRegimeBandExtensions
   20: public static InstrumentRegimeBand FromScore(double score) => score switch
   29: public static string Label(this InstrumentRegimeBand band) => band switch
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeCalculator.cs
```cs
   13: public static class InstrumentRegimeCalculator
   31: public static InstrumentRegimeSnapshot Compute(InstrumentRegimeInputs inputs, DateTime nowUtc)
  284: public int Count { get; }
  285: public ArraySlice(IReadOnlyList<Bar> source, int offset, int count)
  289: public Bar this[int index] => _source[_offset + index];
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeInputs.cs
```cs
   16: public sealed record InstrumentRegimeInputs(
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeSignal.cs
```cs
    8: public enum InstrumentRegimeSignal
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeSnapshot.cs
```cs
   24: public sealed record InstrumentRegimeSnapshot(
   38: public string Label => Band.Label();
   40: public static InstrumentRegimeSnapshot Empty { get; } = new(
```

## src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentSignalScore.cs
```cs
   10: public sealed record InstrumentSignalScore(
```

## src/windows/Core/TradingTerminal.Core/Regime/MarketRegimeCalculator.cs
```cs
   11: public static class MarketRegimeCalculator
   14: public static double Weight(RegimeCategory c) => c switch
   29: public static MarketRegimeSnapshot Compute(RegimeInputs i, double? previousScore, DateTime nowUtc)
  315: public static string LabelFromScore(double score) =>
```

## src/windows/Core/TradingTerminal.Core/Regime/MarketRegimeSnapshot.cs
```cs
    8: public sealed record MarketRegimeSnapshot(
   17: public string Label => State.Label();
   20: public static MarketRegimeSnapshot Empty { get; } = new(
   35: public sealed record RegimeHeaderMetrics(
   46: public static RegimeHeaderMetrics Empty { get; } =
```

## src/windows/Core/TradingTerminal.Core/Regime/RegimeCategory.cs
```cs
    8: public enum RegimeCategory
```

## src/windows/Core/TradingTerminal.Core/Regime/RegimeCategoryScore.cs
```cs
   10: public sealed record RegimeCategoryScore(
```

## src/windows/Core/TradingTerminal.Core/Regime/RegimeInputs.cs
```cs
   10: public sealed class RegimeInputs
   13: public double? Vix { get; init; }
   14: public double? Vix9d { get; init; }
   15: public double? Vix3m { get; init; }
   16: public double? Skew { get; init; }
   17: public double[] SpxCloses { get; init; } = Array.Empty<double>();
   20: public double[] SpyCloses { get; init; } = Array.Empty<double>();
   21: public double[] RspCloses { get; init; } = Array.Empty<double>();
   22: public double[] GldCloses { get; init; } = Array.Empty<double>();
   23: public double[] TltCloses { get; init; } = Array.Empty<double>();
   24: public double[] DxyCloses { get; init; } = Array.Empty<double>();
   25: public double? HygPrice { get; init; }
   26: public double? TltPrice { get; init; }
   29: public IReadOnlyDictionary<string, double[]> SectorCloses { get; init; } =
   33: public double? PutCallRatio { get; init; }
   36: public double? PctAbove200dma { get; init; }
   39: public int? CnnFearGreed { get; init; }
   40: public double? AaiiBull { get; init; }
   41: public double? AaiiBear { get; init; }
   44: public double[] HighYieldOas { get; init; } = Array.Empty<double>();   // BAMLH0A0HYM2
   45: public double[] InvGradeOas { get; init; } = Array.Empty<double>();    // BAMLC0A0CM
   46: public double[] M2 { get; init; } = Array.Empty<double>();             // M2SL (weekly)
   47: public double[] FedBalanceSheet { get; init; } = Array.Empty<double>();// WALCL (weekly)
   48: public double[] FedFunds { get; init; } = Array.Empty<double>();       // FEDFUNDS
   49: public double[] Curve10y2y { get; init; } = Array.Empty<double>();     // T10Y2Y
   50: public double[] Unemployment { get; init; } = Array.Empty<double>();   // UNRATE
   51: public double[] Yield10y { get; init; } = Array.Empty<double>();       // DGS10
   52: public double? Sofr { get; init; }                                     // SOFR latest
```

## src/windows/Core/TradingTerminal.Core/Regime/RegimeState.cs
```cs
    7: public enum RegimeState
   16: public static class RegimeStateExtensions
   19: public static RegimeState FromScore(double score) => score switch
   29: public static string Label(this RegimeState state) => state switch
   40: public static bool IsRiskOff(this RegimeState state) =>
```
