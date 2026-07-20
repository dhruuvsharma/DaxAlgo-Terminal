# TradingTerminal.Core / Strategies — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Strategies/Apex/ApexLineFit.cs
```cs
   15: public sealed record ApexLineFit(
   24: public static ApexLineFit Empty => new(0, 0, 0, 0, 0, 0);
   27: public double SlopeTStat => NeweyWestStandardError > 1e-300 ? Slope / NeweyWestStandardError : 0.0;
```

## src/linux/Core/TradingTerminal.Core/Strategies/Apex/ApexSnapshotV2.cs
```cs
   17: public sealed record ApexSignalState(
   27: public double TtlRemaining => TtlMs > 1e-9 ? Math.Clamp(1.0 - AgeMs / TtlMs, 0.0, 1.0) : 0.0;
   73: public sealed record ApexSnapshotV2(
```

## src/linux/Core/TradingTerminal.Core/Strategies/Apex/ApexTradeRecord.cs
```cs
   17: public sealed record ApexTradeRecord(
```

## src/linux/Core/TradingTerminal.Core/Strategies/Apex/ApexV2Options.cs
```cs
   15: public enum ApexBarMode
   31: public readonly record struct ApexTtlMultipliers(
   40: public static ApexTtlMultipliers Default => new(DeltaFootprint: 1.5, ObiTapeSpeed: 0.5, PocLines: 3.0);
   58: public sealed record ApexV2Options
   66: public double ReferenceSpanSeconds { get; init; } = 60.0;
   70: public ApexBarMode BarMode { get; init; } = ApexBarMode.Time;
   76: public long VolumeBarSize { get; init; } = 2_000;
   80: public ApexTtlMultipliers TtlMultipliers { get; init; } = ApexTtlMultipliers.Default;
   84: public double EwDelta { get; init; } = 0.9;
   90: public int? NeweyWestLag { get; init; }
   96: public int CovarianceWindow { get; init; } = 1_500;
  103: public int WeightRecalcEveryBars { get; init; } = 50;
  106: public int ForwardReturnHorizon { get; init; } = 5;
  109: public int KyleWindow { get; init; } = 50;
  116: public int IsotonicMinSamples { get; init; } = 500;
  122: public int BootstrapSampleThreshold { get; init; } = 500;
  129: public double KellyFraction { get; init; } = 0.25;
  132: public double KellyFractionOverlap { get; init; } = 0.25;
  135: public double KellyFractionAsian { get; init; } = 0.10;
  138: public double RiskFraction { get; init; } = 0.005;
  142: public int RowsPerBarTarget { get; init; } = 20;
  148: public double? TickSizeOverride { get; init; }
  151: public double ImbalanceRatio { get; init; } = 3.0;
  155: public int VpinLookbackBuckets { get; init; } = 50;
  159: public double StopSigmaCoefficient { get; init; } = 1.5;
  162: public double TargetSigmaCoefficient { get; init; } = 2.25;
  168: public double ValueAreaSigmaCoefficient { get; init; } = 1.0;
  172: public double AbsorptionVolumeFraction { get; init; } = 0.5;
  176: public double CompositeThreshold { get; init; } = 1.0;
  179: public IReadOnlyDictionary<string, double> RegimeMultipliers { get; init; } =
  191: public bool TradeAsian { get; init; }
  194: public bool TradeLondon { get; init; } = true;
  197: public bool TradeNewYork { get; init; } = true;
  200: public bool TradeLondonNy { get; init; } = true;
  204: public int CooldownSeconds { get; init; } = 30;
  207: public double MaxDailyLossFraction { get; init; } = 0.02;
  210: public double MaxDrawdownFraction { get; init; } = 0.05;
  214: public double CommissionPerSide { get; init; }
  217: public double SpreadCostTicks { get; init; } = 1.0;
  223: public double SlippageCoefficient { get; init; } = 1.0;
  227: public int PredictedNodeHorizon { get; init; } = 5;
  234: public double PredictionExitMinConfidence { get; init; } = 0.3;
  237: public double KalmanProcessNoise { get; init; } = 1e-4;
  240: public double KalmanMeasurementNoise { get; init; } = 1e-2;
  244: public double HawkesBaselineMu { get; init; }
  247: public double HawkesAlpha { get; init; } = 0.3;
  250: public double HawkesBeta { get; init; } = 0.5;
  253: public static ApexV2Options Default => new();
  263: public static ApexV2Options Backtest => Default with
```

## src/linux/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCompiler.cs
```cs
   15: public interface IStrategyCompiler
   17:     StrategyCompileResult Compile(StrategyScript script);
```

## src/linux/Core/TradingTerminal.Core/Strategies/Authoring/StrategyCompileResult.cs
```cs
   12: public sealed record StrategyCompileResult(
   17: public IEnumerable<StrategyDiagnostic> Errors =>
   20: public static StrategyCompileResult Failed(IReadOnlyList<StrategyDiagnostic> diagnostics) =>
   23: public static StrategyCompileResult Succeeded(
```

## src/linux/Core/TradingTerminal.Core/Strategies/Authoring/StrategyDiagnostic.cs
```cs
    4: public enum StrategyDiagnosticSeverity
   16: public sealed record StrategyDiagnostic(
   23: public override string ToString() =>
```

## src/linux/Core/TradingTerminal.Core/Strategies/Authoring/StrategyScript.cs
```cs
   13: public sealed record StrategyScript(
```

## src/linux/Core/TradingTerminal.Core/Strategies/IStrategyFactory.cs
```cs
    7: public interface IStrategyFactory
    9:     IReadOnlyList<ITradingStrategy> All { get; }
   15:     StrategyHost Create(string strategyId);
```

## src/linux/Core/TradingTerminal.Core/Strategies/ITradingStrategy.cs
```cs
   10: public interface ITradingStrategy
   13:     string Id { get; }
   23:     string? BacktestStrategyId => null;
   25:     string DisplayName { get; }
   27:     string Description { get; }
   36:     StrategyDataRequirement DataRequirement =>
   37:     StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
   44:     string? ResearchPaperUrl => null;
   53:     IReadOnlyList<AssetClass> AssetClasses => Array.Empty<AssetClass>();
   61:     StrategyAssetScope AssetScope => StrategyAssetScope.SingleAsset;
   71:     IReadOnlyList<BrokerKind> SupportedBrokers => StrategyBrokerCapability.ForRequirement(DataRequirement);
```

## src/linux/Core/TradingTerminal.Core/Strategies/Parameters/ParameterKind.cs
```cs
    9: public enum ParameterKind
```

## src/linux/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameter.cs
```cs
   14: public sealed record StrategyParameter
   17: public required string Key { get; init; }
   20: public required string DisplayName { get; init; }
   23: public ParameterKind Kind { get; init; }
   26: public object? Default { get; init; }
   29: public double? Min { get; init; }
   32: public double? Max { get; init; }
   35: public double? Step { get; init; }
   38: public IReadOnlyList<string>? Choices { get; init; }
   41: public string? Description { get; init; }
   44: public string? Group { get; init; }
   47: public string? Unit { get; init; }
   52: public static StrategyParameter Int(
   63: public static StrategyParameter Number(
   74: public static StrategyParameter Bool(
   83: public static StrategyParameter Choice(
   92: public static StrategyParameter Text(
```

## src/linux/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameterSchema.cs
```cs
   11: public sealed class StrategyParameterSchema
   14: public static StrategyParameterSchema Empty { get; } = new(Array.Empty<StrategyParameter>());
   16: public StrategyParameterSchema(IEnumerable<StrategyParameter> parameters)
   31: public StrategyParameterSchema(params StrategyParameter[] parameters)
   36: public IReadOnlyList<StrategyParameter> Parameters { get; }
   38: public bool IsEmpty => Parameters.Count == 0;
   40: public StrategyParameter? Find(string key) =>
   44: public StrategyParameters CreateDefaults() => new(this);
```

## src/linux/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameters.cs
```cs
   15: public sealed class StrategyParameters
   17: public StrategyParameters(StrategyParameterSchema schema, IReadOnlyDictionary<string, object?>? values = null)
   37: public StrategyParameterSchema Schema { get; }
   40: public void Set(string key, object? value)
   46: public int GetInt(string key) => (int)GetLong(key);
   48: public long GetLong(string key) =>
   51: public double GetDouble(string key) =>
   54: public bool GetBool(string key) =>
   57: public string GetString(string key) =>
   61: public object? GetRaw(string key) => _values[Require(key).Key];
   64: public IReadOnlyDictionary<string, object?> ToDictionary() =>
   72: public IReadOnlyList<string> Validate()
```

## src/linux/Core/TradingTerminal.Core/Strategies/StrategyAssetScope.cs
```cs
    9: public enum StrategyAssetScope
```

## src/linux/Core/TradingTerminal.Core/Strategies/StrategyBrokerCapability.cs
```cs
   12: public static class StrategyBrokerCapability
   19: public static readonly IReadOnlyList<BrokerKind> TapeBrokers = new[]
   30: public static readonly IReadOnlyList<BrokerKind> DepthBrokers = new[]
   48: public static IReadOnlyList<BrokerKind> ForRequirement(StrategyDataRequirement requirement)
```

## src/linux/Core/TradingTerminal.Core/Strategies/StrategyDataRequirement.cs
```cs
   22: public enum StrategyDataRequirement
```

## src/linux/Core/TradingTerminal.Core/Strategies/StrategyFactoryRegistration.cs
```cs
    8: public sealed record StrategyFactoryRegistration(
```

## src/linux/Core/TradingTerminal.Core/Strategies/StrategyHost.cs
```cs
    8: public sealed record StrategyHost(
```
