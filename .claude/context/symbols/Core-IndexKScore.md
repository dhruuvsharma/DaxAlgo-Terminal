# TradingTerminal.Core / IndexKScore — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/IndexKScore/IndexComponentCatalog.cs
```cs
    6: public sealed record IndexFamily(
   22: public static class IndexComponentCatalog
   26: public static IReadOnlyList<IndexFamily> All { get; } = new IndexFamily[]
```

## src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreAggregator.cs
```cs
   10: public sealed record IndexComponent(
   22: public sealed class IndexKScoreAggregator
   28: public double TMin { get; }
   29: public double TMax { get; }
   30: public int MinPierceCount { get; }
   31: public double CumKThreshold { get; }
   33: public IndexKScoreAggregator(
   65: public double ComputeThreshold(double weight)
   73: public IndexSnapshot? Update(string symbol, IndexKScoreCalculator.Snapshot snapshot)
   81: public IndexSnapshot BuildAggregate(DateTime asOfUtc)
  130: public void Reset()
  139: public IReadOnlyDictionary<string, double> ThresholdsBySymbol =>
  144: public IndexComponent Component { get; init; } = null!;
  145: public double Threshold { get; init; }
  146: public bool HasOutput { get; set; }
  147: public IndexKScoreCalculator.Snapshot Latest { get; set; }
  151: public sealed record ComponentSnapshot(
  166: public sealed record IndexSnapshot(
```

## src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreCalculator.cs
```cs
   19: public sealed class IndexKScoreCalculator
   21: public IndexKScoreParameters Parameters { get; }
   66: public IndexKScoreCalculator(IndexKScoreParameters parameters)
   72: public bool HasOutput { get; private set; }
   77: public readonly record struct Snapshot(
   87: public readonly record struct SignalBreakdown(
  104: public Snapshot? OnBar(Bar bar)
  301: public void Reset()
```

## src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreParameters.cs
```cs
    9: public sealed record IndexKScoreParameters
   11: public const double WeightSumTolerance = 0.001;
   14: public int RsiLength { get; init; } = 14;
   15: public double RsiOverbought { get; init; } = 70;
   16: public double RsiOversold { get; init; } = 30;
   17: public int MacdFast { get; init; } = 12;
   18: public int MacdSlow { get; init; } = 26;
   19: public int MacdSignal { get; init; } = 9;
   20: public int CciLength { get; init; } = 20;
   21: public int Ma9Length { get; init; } = 9;
   22: public int Ma21Length { get; init; } = 21;
   23: public int Ma50Length { get; init; } = 50;
   24: public int Ma3Fast { get; init; } = 8;
   25: public int Ma3Mid { get; init; } = 21;
   26: public int Ma3Slow { get; init; } = 50;
   27: public bool VwapSession { get; init; } = true;
   28: public double SupertrendFactor { get; init; } = 3.0;
   29: public int SupertrendAtrLength { get; init; } = 10;
   30: public int AtrLength { get; init; } = 14;
   31: public int AtrRegLength { get; init; } = 50;
   32: public int StdLength { get; init; } = 20;
   33: public int PocLookback { get; init; } = 50;
   34: public int TrdLength { get; init; } = 20;
   35: public int DeltaLookback { get; init; } = 20;
   39: public double WeightSuperTrend { get; init; } = 0.12;
   40: public double WeightMacd { get; init; } = 0.11;
   41: public double WeightRsi { get; init; } = 0.10;
   42: public double WeightVwap { get; init; } = 0.09;
   43: public double Weight3Ma { get; init; } = 0.09;
   44: public double WeightCumDelta { get; init; } = 0.08;
   45: public double WeightVolBs { get; init; } = 0.08;
   46: public double WeightCci { get; init; } = 0.07;
   47: public double WeightMa50 { get; init; } = 0.06;
   48: public double WeightMa21 { get; init; } = 0.05;
   49: public double WeightPocPos { get; init; } = 0.05;
   50: public double WeightTrd { get; init; } = 0.04;
   51: public double WeightMa9 { get; init; } = 0.03;
   52: public double WeightDelta { get; init; } = 0.02;
   53: public double WeightAtrReg { get; init; } = 0.01;
   55: public double WeightSum =>
   60: public void Validate()
```
