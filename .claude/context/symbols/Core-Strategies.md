# TradingTerminal.Core / Strategies — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IAiKeyResolver.cs
```cs
    9: public interface IAiKeyResolver
   13:     string? Resolve(string providerId);
   16:     public static IAiKeyResolver Null { get; } = new NullAiKeyResolver();
   21: public string? Resolve(string providerId) => null;
   29: public interface IAiKeyStore
   32:     IReadOnlyCollection<string> ConfiguredProviders { get; }
   34:     bool HasKey(string providerId);
   35:     void Set(string providerId, string apiKey);
   36:     void Remove(string providerId);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCodegenClient.cs
```cs
    4: public enum CodegenRole
   11: public sealed record CodegenMessage(CodegenRole Role, string Content);
   19: public sealed record StrategyCodegenRequest(string SystemContext, IReadOnlyList<CodegenMessage> Messages);
   27: public sealed record StrategyCodegenResponse(bool Success, string? Code, string? RawText, string? Error)
   29: public static StrategyCodegenResponse Ok(string code, string rawText) => new(true, code, rawText, null);
   30: public static StrategyCodegenResponse Fail(string error) => new(false, null, null, error);
   45: public interface IStrategyCodegenClient
   49:     string ProviderId { get; }
   52:     string DisplayName { get; }
   56:     bool IsAvailable { get; }
   58:     Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCompiler.cs
```cs
   18: public interface IStrategyCompiler
   20:     StrategyCompileResult Compile(StrategyScript script);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyCompileResult.cs
```cs
   12: public sealed record StrategyCompileResult(
   17: public IEnumerable<StrategyDiagnostic> Errors =>
   20: public static StrategyCompileResult Failed(IReadOnlyList<StrategyDiagnostic> diagnostics) =>
   23: public static StrategyCompileResult Succeeded(
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyDiagnostic.cs
```cs
    4: public enum StrategyDiagnosticSeverity
   16: public sealed record StrategyDiagnostic(
   23: public override string ToString() =>
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyScript.cs
```cs
   13: public sealed record StrategyScript(
```

## src/windows/Core/TradingTerminal.Core/Strategies/IStrategyFactory.cs
```cs
    7: public interface IStrategyFactory
    9:     IReadOnlyList<ITradingStrategy> All { get; }
   15:     StrategyHost Create(string strategyId);
```

## src/windows/Core/TradingTerminal.Core/Strategies/ITradingStrategy.cs
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

## src/windows/Core/TradingTerminal.Core/Strategies/Parameters/ParameterKind.cs
```cs
    9: public enum ParameterKind
```

## src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameter.cs
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

## src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameterSchema.cs
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

## src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameters.cs
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

## src/windows/Core/TradingTerminal.Core/Strategies/StrategyAssetScope.cs
```cs
    9: public enum StrategyAssetScope
```

## src/windows/Core/TradingTerminal.Core/Strategies/StrategyBrokerCapability.cs
```cs
   12: public static class StrategyBrokerCapability
   19: public static readonly IReadOnlyList<BrokerKind> TapeBrokers = new[]
   30: public static readonly IReadOnlyList<BrokerKind> DepthBrokers = new[]
   48: public static IReadOnlyList<BrokerKind> ForRequirement(StrategyDataRequirement requirement)
```

## src/windows/Core/TradingTerminal.Core/Strategies/StrategyDataRequirement.cs
```cs
   22: public enum StrategyDataRequirement
```

## src/windows/Core/TradingTerminal.Core/Strategies/StrategyFactoryRegistration.cs
```cs
    8: public sealed record StrategyFactoryRegistration(
```

## src/windows/Core/TradingTerminal.Core/Strategies/StrategyHost.cs
```cs
    8: public sealed record StrategyHost(
```
