# DaxAlgo.Codegen — public API surface

Generated 2026-07-13. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/DaxAlgo.Codegen/AgentCliCodegenClient.cs
```cs
    9: public sealed record AgentCliAdapter(
   23: public static AgentCliAdapter ClaudeCode { get; } =
   28: public static AgentCliAdapter Codex { get; } =
   31: public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];
   35: public IReadOnlyList<string> ArgumentsFor(string? model)
   53: public sealed class AgentCliCodegenClient : IStrategyCodegenClient
   60: public AgentCliCodegenClient(
   70: public string ProviderId => _adapter.ProviderId;
   71: public string DisplayName => _adapter.DisplayName;
   72: public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;
   75: public string Model => _model ?? string.Empty;
   76: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   78: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Tools/DaxAlgo.Codegen/AiModelCatalog.cs
```cs
   10: public static class AiModelCatalog
   12: public static IReadOnlyList<string> For(string providerId) => providerId.ToLowerInvariant() switch
   25: public static IReadOnlyList<string> Offer(string providerId, string? configuredModel)
```

## src/windows/Tools/DaxAlgo.Codegen/AiStrategyBuilder.cs
```cs
   13: public interface IAiStrategyBuilder
   17:     IReadOnlyList<IStrategyCodegenClient> Providers { get; }
   20:     IStrategyCodegenClient? DefaultProvider { get; }
   24:     IStrategyCodegenClient? WithModel(string providerId, string? model);
   28:     IReadOnlyList<string> ModelsFor(string providerId);
   33:     StrategyBuildSession StartSession(IStrategyCodegenClient provider, string strategyId, string displayName);
   38:     Task<StrategyBuildLoopResult> BuildAsync(
   39:     IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
   40:     CancellationToken ct = default);
   43: public sealed class AiStrategyBuilder(
   49: public IReadOnlyList<IStrategyCodegenClient> Providers => factory.BuildAll();
   51: public IStrategyCodegenClient? DefaultProvider => factory.SelectDefault();
   53: public IStrategyCodegenClient? WithModel(string providerId, string? model) => factory.Build(providerId, model);
   55: public IReadOnlyList<string> ModelsFor(string providerId) => factory.ModelsFor(providerId);
   57: public StrategyBuildSession StartSession(IStrategyCodegenClient provider, string strategyId, string displayName) =>
   60: public Task<StrategyBuildLoopResult> BuildAsync(
```

## src/windows/Tools/DaxAlgo.Codegen/AnthropicCodegenClient.cs
```cs
   14: public sealed class AnthropicCodegenClient : IStrategyCodegenClient
   24: public AnthropicCodegenClient(HttpClient http, string baseUrl, string model, string? apiKey)
   32: public string ProviderId => "anthropic";
   33: public string DisplayName => "Anthropic (API key)";
   34: public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
   35: public string Model => _model;
   36: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   40: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   63: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Tools/DaxAlgo.Codegen/CodegenCodeExtractor.cs
```cs
   15: public static partial class CodegenCodeExtractor
   31: public static string Extract(string? reply)
   41: public static IReadOnlyList<StrategyFile> ExtractFiles(string? reply)
```

## src/windows/Tools/DaxAlgo.Codegen/FakeCodegenClient.cs
```cs
   11: public sealed class FakeCodegenClient : IStrategyCodegenClient
   18: public FakeCodegenClient(params string[] replies)
   23: public string ProviderId => "fake";
   24: public string DisplayName => "Fake (deterministic)";
   25: public bool IsAvailable => true;
   28: public int CallCount { get; private set; }
   31: public CodegenUsage Usage { get; init; } = new(100, 50);
   33: public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
   47: public const string DefaultKernel = """
   49: public sealed class GeneratedStrategy(Contract contract) : IBacktestStrategy
   55: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   57: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   66: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   67: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
```

## src/windows/Tools/DaxAlgo.Codegen/OpenAiCompatibleCodegenClient.cs
```cs
   15: public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
   28: public OpenAiCompatibleCodegenClient(
   43: public string ProviderId { get; }
   44: public string DisplayName { get; }
   46: public bool IsAvailable =>
   50: public string Model => _model;
   51: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   55: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   78: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyBuildSession.cs
```cs
    8: public enum BuildTurnKind
   34: public sealed record StrategyBuildTurn(
   43: public bool Success => Kind == BuildTurnKind.Compiled;
   62: public sealed class StrategyBuildSession
   86: public IStrategyCodegenClient Provider { get; }
   87: public string SystemContext { get; }
   88: public string StrategyId { get; }
   89: public string DisplayName { get; }
   90: public int MaxFixAttempts { get; }
   93: public IReadOnlyList<CodegenMessage> Transcript => _messages;
   96: public IReadOnlyList<StrategyFile> Files { get; private set; } = [];
   99: public CodegenUsage TotalUsage { get; private set; } = CodegenUsage.None;
  108: public async Task<StrategyBuildTurn> SendAsync(
  191: public void SyncEditedFiles(IReadOnlyList<StrategyFile> files) => Files = files;
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenClientFactory.cs
```cs
   19: public sealed class StrategyCodegenClientFactory
   28: public StrategyCodegenClientFactory(Func<HttpClient> httpFactory, AiCodegenOptions options, Func<string, string?> keyResolver)
   38: public IReadOnlyList<IStrategyCodegenClient> BuildAll()
   62: public IStrategyCodegenClient? Build(string providerId, string? model)
   77: public IReadOnlyList<string> ModelsFor(string providerId) =>
   82: public IStrategyCodegenClient? SelectDefault()
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenOrchestrator.cs
```cs
   10: public sealed record StrategyBuildLoopResult(
   32: public sealed class StrategyCodegenOrchestrator(IStrategyCompiler compiler, ILogger<StrategyCodegenOrchestrator>? logger = null)
   39: public StrategyBuildSession CreateSession(
   48: public async Task<StrategyBuildLoopResult> BuildAsync(
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenServiceCollectionExtensions.cs
```cs
   11: public static class StrategyCodegenServiceCollectionExtensions
   22: public static IServiceCollection AddStrategyCodegen(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyContextPack.cs
```cs
   11: public sealed class StrategyContextPack
   16: public string SystemPrompt { get; }
   22: public static StrategyContextPack Load()
```
