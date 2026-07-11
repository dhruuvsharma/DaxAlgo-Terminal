# TradingTerminal.Core / Ml — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Ml/DepthStepSampler.cs
```cs
   14: public sealed class DepthStepSampler
   22: public DepthStepSampler(TimeSpan step, int statsDepth, long sweepSize, TimeSpan? maxGap = null)
   35: public DateTime LastBoundaryUtc { get; private set; } = DateTime.MinValue;
   39: public int Add(DepthSnapshot snapshot, List<OrderBookStepSummary> output)
   62: public void Reset()
```

## src/windows/Core/TradingTerminal.Core/Ml/EwmaForecaster.cs
```cs
   12: public sealed class EwmaForecaster : IOnlineForecaster
   15: public const string ForecasterKind = "ewma";
   23: public EwmaForecaster(int dimensions, double alpha = 0.05)
   31: public string Kind => ForecasterKind;
   32: public int Dimensions => _d;
   33: public long Samples => _samples;
   36: public double Predict(IReadOnlyList<double> features) => _ewma;
   38: public void Update(IReadOnlyList<double> features, double target)
   54: public ForecasterState SaveState() =>
   57: public void LoadState(ForecasterState state)
```

## src/windows/Core/TradingTerminal.Core/Ml/FactorComputation.cs
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

## src/windows/Core/TradingTerminal.Core/Ml/FootprintNextBarPredictor.cs
```cs
   24: public sealed class FootprintNextBarPredictor
   33: public const string ModelKind = "footprint-nextbar";
   64: public FootprintNextBarPredictor(double tickSize, FootprintPredictorOptions? options = null)
   82: public ForecastAccuracy MlAccuracy => _mlMetrics.Snapshot();
   86: public ForecastAccuracy BaselineAccuracy => _baselineMetrics.Snapshot();
   89: public long SamplesSeen => _samplesSeen;
   93: public bool IsReady => _bank[0][0].Samples >= _options.MinSamplesReady;
   97: public IReadOnlyList<FootprintForecastBar> LastForecast => _lastForecast;
  106: public IReadOnlyList<FootprintForecastBar> OnBarSealed(FootprintBarSummary bar, double baselineNextPoc)
  162: public void Reset()
  186: public ModelArtifact CreateArtifact(string instrumentKey, string timeframe)
  233: public bool TryRestore(ModelArtifact artifact)
```

## src/windows/Core/TradingTerminal.Core/Ml/FootprintPredictionModels.cs
```cs
   25: public sealed record FootprintBarSummary(
   43: public static FootprintBarSummary From(
   59: public sealed record FootprintForecastBar(
   70: public readonly record struct ForecastAccuracy(
   83: public sealed record FootprintPredictorOptions(
```

## src/windows/Core/TradingTerminal.Core/Ml/Forecasters.cs
```cs
   10: public enum LearnerKind
   31: public static class Forecasters
   36: public static IOnlineForecaster Create(LearnerKind kind, int dimensions, double lambda) => kind switch
   47: public static string Tag(LearnerKind kind) => kind switch
   57: public static LearnerKind Parse(string tag) => tag switch
   67: public static string DisplayName(LearnerKind kind) => kind switch
   79: public static IReadOnlyList<LearnerOption> DirectionChoices { get; } = new[]
   88: public sealed record LearnerOption(LearnerKind Kind, string Name);
```

## src/windows/Core/TradingTerminal.Core/Ml/IModelRegistry.cs
```cs
    9: public sealed record ModelKey(string ModelKind, string InstrumentKey, string Timeframe, string Algorithm);
   13: public sealed record StoredModel(string ModelId, int Version, string Sha256, DateTime CreatedUtc);
   17: public sealed record StoredModelInfo(
   36: public interface IModelRegistry
   40:     StoredModel Save(ModelArtifact artifact);
   43:     ModelArtifact? Load(string modelId);
   47:     ModelArtifact? LoadLatest(ModelKey key);
   51:     IReadOnlyList<StoredModelInfo> List(ModelKey? filter, int maxRows);
   54:     bool Delete(string modelId);
   57:     int PruneOlderThan(int retentionDays);
```

## src/windows/Core/TradingTerminal.Core/Ml/IOnlineForecaster.cs
```cs
   17: public interface IOnlineForecaster
   21:     string Kind { get; }
   24:     int Dimensions { get; }
   27:     long Samples { get; }
   30:     double Predict(IReadOnlyList<double> features);
   33:     void Update(IReadOnlyList<double> features, double target);
   36:     ForecasterState SaveState();
   40:     void LoadState(ForecasterState state);
   55: public sealed record ForecasterState(
```

## src/windows/Core/TradingTerminal.Core/Ml/ModelArtifact.cs
```cs
   46: public sealed record ModelArtifact(
   63: public const int CurrentSchemaVersion = 1;
   69: public ModelKey Key => new(ModelKind, InstrumentKey, Timeframe, Algorithm);
   72: public BankState? Bank(string name)
   80: public double Scalar(string name, double fallback = 0.0)
   92: public sealed record FeatureContract(int Dimension, IReadOnlyList<string> Names)
   97: public string ComputeHash()
  120: public sealed record BankState(string Name, IReadOnlyList<ForecasterState> Learners);
  125: public readonly record struct ScalarState(string Name, double Value);
  131: public readonly record struct ModelMetrics(
  138: public static readonly ModelMetrics Empty = new(double.NaN, double.NaN, double.NaN, double.NaN, 0);
```

## src/windows/Core/TradingTerminal.Core/Ml/ModelArtifactJson.cs
```cs
   16: public static class ModelArtifactJson
   18: public static readonly JsonSerializerOptions Options = new()
```

## src/windows/Core/TradingTerminal.Core/Ml/OnlineFeatureScaler.cs
```cs
   11: public sealed class OnlineFeatureScaler
   22: public OnlineFeatureScaler(int dimensions, double halfLifeSamples = 64, double clip = 5.0, int passthroughDimensions = 1)
   37: public int Dimensions => _mean.Length;
   38: public long Samples => _samples;
   41: public void Observe(IReadOnlyList<double> raw)
   65: public void Transform(IReadOnlyList<double> raw, double[] destination)
   83: public void Reset()
   93: public FeatureScalerState SaveState()
  103: public void LoadState(FeatureScalerState state)
  116: public sealed record FeatureScalerState(int Dimensions, long Samples, double[] Mean, double[] Variance);
```

## src/windows/Core/TradingTerminal.Core/Ml/OnlineGradientDescent.cs
```cs
   12: public sealed class OnlineGradientDescent : IOnlineForecaster
   15: public const string ForecasterKind = "ogd";
   23: public OnlineGradientDescent(int dimensions, double learningRate = 0.05, double l2 = 1e-4)
   34: public string Kind => ForecasterKind;
   35: public int Dimensions => _d;
   36: public long Samples => _samples;
   38: public double Predict(IReadOnlyList<double> features)
   46: public void Update(IReadOnlyList<double> features, double target)
   54: public ForecasterState SaveState()
   61: public void LoadState(ForecasterState state)
```

## src/windows/Core/TradingTerminal.Core/Ml/OnlineLinearRegression.cs
```cs
   18: public sealed class OnlineLinearRegression : IOnlineForecaster
   21: public const string ForecasterKind = "rls";
   29: public OnlineLinearRegression(int dimensions, double lambda = 0.99, double initialDiagonal = 1e3)
   41: public string Kind => ForecasterKind;
   42: public int Dimensions => _d;
   43: public double Lambda { get; }
   44: public long Samples => _samples;
   45: public IReadOnlyList<double> Coefficients => _beta;
   48: public double Predict(IReadOnlyList<double> features)
   57: public void Update(IReadOnlyList<double> features, double y)
   91: public ForecasterState SaveState()
  105: public void LoadState(ForecasterState state)
```

## src/windows/Core/TradingTerminal.Core/Ml/OnlineLogisticRegression.cs
```cs
   12: public sealed class OnlineLogisticRegression : IOnlineForecaster
   15: public const string ForecasterKind = "logistic";
   23: public OnlineLogisticRegression(int dimensions, double learningRate = 0.1, double l2 = 1e-4)
   34: public string Kind => ForecasterKind;
   35: public int Dimensions => _d;
   36: public long Samples => _samples;
   39: public double Predict(IReadOnlyList<double> features)
   47: public void Update(IReadOnlyList<double> features, double target)
   57: public ForecasterState SaveState()
   64: public void LoadState(ForecasterState state)
```

## src/windows/Core/TradingTerminal.Core/Ml/OrderBookEventLabeler.cs
```cs
    8: public static class OrderBookEventLabeler
   15: public static bool SpreadWidened(double referenceSpread, double maxFutureSpread, double tick, double widenTicks = 1.0)
   21: public static bool DepthDrained(long referenceBid3, long referenceAsk3, long minFutureBid3, long minFutureAsk3, double drainRatio = 0.7)
   28: public static bool SweepJumped(double referenceWorstSweep, double maxFutureWorstSweep, double tick, double jumpRatio = 1.25)
```

## src/windows/Core/TradingTerminal.Core/Ml/OrderBookMicroPredictor.cs
```cs
   26: public sealed class OrderBookMicroPredictor
   34: public const string ModelKind = "orderbook-micro";
   75: public OrderBookMicroPredictor(OrderBookPredictorOptions? options = null)
  105: public ForecastAccuracy MlAccuracy => _mlMetrics.Snapshot();
  110: public ForecastAccuracy BaselineAccuracy => _baselineMetrics.Snapshot();
  112: public EventScore SpreadWidenScore => _spreadScore.Snapshot();
  113: public EventScore DepthDrainScore => _depthScore.Snapshot();
  114: public EventScore SweepJumpScore => _sweepScore.Snapshot();
  117: public long SamplesSeen => _samplesSeen;
  121: public bool IsReady => _directionBank[_flagshipIndex].Samples >= _options.MinSamplesReady;
  125: public double TickSize => _observedTick == double.MaxValue ? DefaultTick : _observedTick;
  129: public OrderBookForecast? LastForecast => _lastForecast;
  136: public OrderBookForecast? OnStep(OrderBookStepSummary step)
  213: public void Reset()
  244: public ModelArtifact CreateArtifact(string instrumentKey, string timeframe)
  299: public bool TryRestore(ModelArtifact artifact)
```

## src/windows/Core/TradingTerminal.Core/Ml/OrderBookPredictionModels.cs
```cs
   36: public sealed record OrderBookStepSummary(
   65: public static OrderBookStepSummary From(
  124: public readonly record struct MicropricePoint(int HorizonSteps, double Microprice);
  131: public sealed record OrderBookForecast(
  144: public readonly record struct EventScore(double Brier, double BaseRate, long ScoredCount);
  160: public sealed record OrderBookPredictorOptions(
  175: public IReadOnlyList<int> Horizons { get; init; } = DefaultHorizons;
  177: public static readonly int[] DefaultHorizons = { 1, 2, 4, 8, 20 };
```

## src/windows/Core/TradingTerminal.Core/Ml/RollingBrierScore.cs
```cs
   10: public sealed class RollingBrierScore
   18: public RollingBrierScore(int window = 200)
   27: public void Score(double probability, bool occurred)
   40: public EventScore Snapshot()
   54: public void Reset()
```

## src/windows/Core/TradingTerminal.Core/Ml/RollingForecastMetrics.cs
```cs
    9: public sealed class RollingForecastMetrics
   19: public RollingForecastMetrics(int window = 100)
   28: public void Score(double predictedDeltaTicks, double realizedDeltaTicks)
   43: public ForecastAccuracy Snapshot()
   57: public void Reset()
```

## src/windows/Core/TradingTerminal.Core/Ml/TripleBarrierLabeler.cs
```cs
   20: public static class TripleBarrierLabeler
   22: public enum Label { Negative = -1, Neutral = 0, Positive = 1 }
   24: public sealed record LabelledBar<TBar>(int Index, TBar Bar, Label Label, int BarsToOutcome);
   33: public static IReadOnlyList<LabelledBar<TBar>> Apply<TBar>(
```
