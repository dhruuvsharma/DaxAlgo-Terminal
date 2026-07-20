# TradingTerminal.Core / Strategies — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/AiModelChoice.cs
```cs
   14: public sealed record AiModelChoice(string ProviderId, string ProviderLabel, string ModelId)
   18: public bool IsAvailable { get; init; } = true;
   22: public string Display => string.IsNullOrEmpty(ModelId) ? ProviderLabel : $"{ModelId} · {ProviderLabel}";
```

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

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IAuthoredStrategyViewComposer.cs
```cs
   18: public interface IAuthoredStrategyViewComposer
   23:     object ComposeView(ITradingStrategy descriptor);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCodegenClient.cs
```cs
    4: public enum CodegenRole
   16: public enum CodegenEffort
   32: public static class CodegenEfforts
   35: public static string? Wire(this CodegenEffort effort) => effort switch
   47: public static CodegenEffort Parse(string? value) => value?.Trim().ToLowerInvariant() switch
   59: public sealed record CodegenMessage(CodegenRole Role, string Content);
   72: public sealed record CodegenUsage(int InputTokens, int OutputTokens, int CachedInputTokens = 0)
   74: public static CodegenUsage None { get; } = new(0, 0);
   76: public int TotalTokens => InputTokens + OutputTokens;
   79: public bool IsReported => InputTokens > 0 || OutputTokens > 0;
   81: public CodegenUsage Add(CodegenUsage? other) => other is null
   94: public sealed record StrategyCodegenRequest(string SystemContext, IReadOnlyList<CodegenMessage> Messages);
  108: public sealed record StrategyCodegenResponse(
  117: public IReadOnlyList<StrategyFile> FileList => Files ?? (string.IsNullOrWhiteSpace(Code)
  122: public bool HasFiles => FileList.Count > 0;
  124: public static StrategyCodegenResponse Ok(string code, string rawText) => new(true, code, rawText, null);
  126: public static StrategyCodegenResponse Ok(IReadOnlyList<StrategyFile> files, string rawText, CodegenUsage? usage = null) =>
  130: public static StrategyCodegenResponse Reply(string rawText, CodegenUsage? usage = null) =>
  133: public static StrategyCodegenResponse Fail(string error) => new(false, null, null, error);
  142: public abstract record CodegenEvent
  147: public sealed record TextDelta(string Text) : CodegenEvent;
  151: public sealed record UsageUpdate(CodegenUsage Usage) : CodegenEvent;
  154: public sealed record Completed(StrategyCodegenResponse Response) : CodegenEvent;
  169: public interface IStrategyCodegenClient
  173:     string ProviderId { get; }
  176:     string DisplayName { get; }
  180:     bool IsAvailable { get; }
  184:     string Model => string.Empty;
  188:     CodegenEffort Effort => CodegenEffort.Default;
  192:     IReadOnlyList<string> KnownModels => [];
  197:     Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
  198:     Task.FromResult<IReadOnlyList<string>>([]);
  200:     Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default);
  213:     async IAsyncEnumerable<CodegenEvent> StreamAsync(
  214:     StrategyCodegenRequest request,
  217:     var response = await GenerateAsync(request, ct).ConfigureAwait(false);
  218:     if (response.Usage is { IsReported: true } usage) yield return new CodegenEvent.UsageUpdate(usage);
  219:     yield return new CodegenEvent.Completed(response);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCompiler.cs
```cs
   18: public interface IStrategyCompiler
   20:     StrategyCompileResult Compile(StrategyScript script);
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyBuildEffort.cs
```cs
   10: public enum StrategyBuildEffort
   27: public static class StrategyBuildEfforts
   31: public static string Wire(this StrategyBuildEffort effort) => effort switch
   41: public static StrategyBuildEffort Parse(string? value) => value?.Trim().ToLowerInvariant() switch
   60: public sealed record StrategyBuildProfile(int MaxSkills, int MaxFixAttempts, bool SelfReview, bool BacktestSmoke)
   63: public static StrategyBuildProfile For(StrategyBuildEffort effort) => effort switch
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyCompileResult.cs
```cs
   23: public sealed record AuthoredStrategyAssembly(
   33: public bool HasLiveWindow => DescriptorType is not null && ViewModelType is not null && ViewType is not null;
   38: public bool CanComposeLiveWindow => DescriptorType is not null && ViewModelType is not null;
   43: public IReadOnlyList<string> MissingForCatalog =>
   57: public sealed record StrategyCompileResult(
   63: public IEnumerable<StrategyDiagnostic> Errors =>
   66: public static StrategyCompileResult Failed(IReadOnlyList<StrategyDiagnostic> diagnostics) =>
   69: public static StrategyCompileResult Succeeded(
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyDiagnostic.cs
```cs
    4: public enum StrategyDiagnosticSeverity
   21: public sealed record StrategyDiagnostic(
   30: public string Location => string.IsNullOrEmpty(File)
   34: public override string ToString() =>
```

## src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyScript.cs
```cs
   10: public sealed record StrategyFile(string Name, string Content)
   13: public const string DefaultName = "Strategy.cs";
   26: public sealed record StrategyScript(
   32: public StrategyScript(string id, string displayName, string sourceCode)
```

## src/windows/Core/TradingTerminal.Core/Strategies/IStrategyFactory.cs
```cs
   13: public interface IStrategyFactory
   15:     IReadOnlyList<ITradingStrategy> All { get; }
   21:     StrategyHost Create(string strategyId);
   28:     void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration);
   31:     event EventHandler<StrategyCatalogChange>? Changed;
   36: public sealed record StrategyCatalogChange(ITradingStrategy Strategy, bool Replaced);
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
