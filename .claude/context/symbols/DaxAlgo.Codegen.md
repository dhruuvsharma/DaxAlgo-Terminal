# DaxAlgo.Codegen — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/DaxAlgo.Codegen/AgentCliCodegenClient.cs
```cs
   10: public sealed record AgentCliAdapter(
   26: public static AgentCliAdapter ClaudeCode { get; } =
   37: public static AgentCliAdapter Codex { get; } =
   40: public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];
   45: public IReadOnlyList<string>? StreamFlags { get; init; }
   49: public IReadOnlyList<string> ArgumentsFor(
   82: public sealed class AgentCliCodegenClient : IStrategyCodegenClient
   90: public AgentCliCodegenClient(
  103: public string ProviderId => _adapter.ProviderId;
  104: public string DisplayName => _adapter.DisplayName;
  105: public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;
  108: public string Model => _model ?? string.Empty;
  109: public CodegenEffort Effort => _effort;
  110: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
  118: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  251: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Tools/DaxAlgo.Codegen/AiModelCatalog.cs
```cs
   16: public static class AiModelCatalog
   29: public static IReadOnlyList<string> For(string providerId) => providerId.ToLowerInvariant() switch
   40: public static IReadOnlyList<string> Offer(string providerId, string? configuredModel)
   55: public static bool SupportsEffort(string providerId) => providerId.ToLowerInvariant() switch
```

## src/windows/Tools/DaxAlgo.Codegen/AiStrategyBuilder.cs
```cs
   13: public interface IAiStrategyBuilder
   17:     IReadOnlyList<IStrategyCodegenClient> Providers { get; }
   20:     IStrategyCodegenClient? DefaultProvider { get; }
   25:     IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort);
   29:     IReadOnlyList<string> ModelsFor(string providerId);
   37:     IReadOnlyList<AiModelChoice> AllModels();
   45:     StrategyBuildSession StartSession(
   46:     IStrategyCodegenClient provider, string strategyId, string displayName,
   47:     IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
   48:     StrategyBuildProfile? profile = null);
   53:     Task<StrategyBuildLoopResult> BuildAsync(
   54:     IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
   55:     CancellationToken ct = default);
   58: public sealed class AiStrategyBuilder(
   64: public IReadOnlyList<IStrategyCodegenClient> Providers => factory.BuildAll();
   66: public IStrategyCodegenClient? DefaultProvider => factory.SelectDefault();
   68: public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
   71: public IReadOnlyList<string> ModelsFor(string providerId) => factory.ModelsFor(providerId);
   73: public IReadOnlyList<AiModelChoice> AllModels()
   95: public StrategyBuildSession StartSession(
  102: public Task<StrategyBuildLoopResult> BuildAsync(
```

## src/windows/Tools/DaxAlgo.Codegen/AnthropicCodegenClient.cs
```cs
   14: public sealed class AnthropicCodegenClient : IStrategyCodegenClient
   25: public AnthropicCodegenClient(
   36: public string ProviderId => "anthropic";
   37: public string DisplayName => "Anthropic (API key)";
   38: public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
   39: public string Model => _model;
   40: public CodegenEffort Effort => _effort;
   41: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   45: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   72: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  145: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
  258: public WireCacheControl? CacheControl { get; init; }
  263: public static WireCacheControl Ephemeral { get; } = new("ephemeral");
```

## src/windows/Tools/DaxAlgo.Codegen/AnthropicStreamParser.cs
```cs
   21: public string Text => _text.ToString();
   23: public CodegenUsage Usage => new(_input, _output, _cached);
   29: public IEnumerable<CodegenEvent> Consume(JsonElement evt)
   81: public static async IAsyncEnumerable<JsonElement> ReadAsync(
```

## src/windows/Tools/DaxAlgo.Codegen/CliWorkspaceLauncher.cs
```cs
   11: public sealed record CliLaunchResult(bool Success, string Message, string WorkspacePath);
   20: public interface ICliWorkspaceLauncher
   24:     IReadOnlyList<AgentCliAdapter> AvailableClis();
   28:     CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort);
   37: public sealed class CliWorkspaceLauncher(
   42: public IReadOnlyList<AgentCliAdapter> AvailableClis() =>
   45: public CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort)
  306: public sealed class MyStrategy : IBacktestStrategy
  308: public static StrategyParameterSchema Schema { get; } = new(
  312: public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
  319: public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }
  321: public MyStrategy(Contract contract, int lookback, double threshold)
  328: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
  331: public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  339: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  341: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Tools/DaxAlgo.Codegen/CodegenCodeExtractor.cs
```cs
   15: public static partial class CodegenCodeExtractor
   31: public static string Extract(string? reply)
   44: public static string StripCode(string? reply)
   66: public static IReadOnlyList<StrategyFile> ExtractFiles(string? reply)
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
   35: public StrategyCodegenRequest? LastRequest { get; private set; }
   37: public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
   52: public const string DefaultKernel = """
   54: public sealed class GeneratedStrategy(Contract contract) : IBacktestStrategy
   60: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   62: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   71: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   72: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
```

## src/windows/Tools/DaxAlgo.Codegen/OpenAiCompatibleCodegenClient.cs
```cs
   15: public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
   28: public OpenAiCompatibleCodegenClient(
   45: public string ProviderId { get; }
   46: public string DisplayName { get; }
   48: public bool IsAvailable =>
   52: public string Model => _model;
   53: public CodegenEffort Effort => _effort;
   54: public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);
   58: public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
   86: public async IAsyncEnumerable<CodegenEvent> StreamAsync(
  206: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyBacktestSmoke.cs
```cs
   20: public static class StrategyBacktestSmoke
   35: public static async Task<string?> RunAsync(BacktestStrategyOption option, CancellationToken ct = default)
   91: public DateTime UtcNow { get; private set; } = start;
   92: public void Advance(TimeSpan by) => UtcNow += by;
   99: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
  102: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) => Task.CompletedTask;
  104: public IObservable<OrderEvent> OrderEvents { get; } = new NeverObservable();
  108: public IDisposable Subscribe(IObserver<OrderEvent> observer) => new Nothing();
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyBuildSession.cs
```cs
    8: public enum BuildTurnKind
   34: public sealed record StrategyBuildTurn(
   43: public bool Success => Kind == BuildTurnKind.Compiled;
   62: public sealed class StrategyBuildSession
  109: public IStrategyCodegenClient Provider { get; }
  112: public string BasePack { get; }
  117: public string SystemContext { get; private set; }
  120: public IReadOnlyList<StrategySkill> LoadedSkills { get; private set; } = [];
  121: public string StrategyId { get; }
  122: public string DisplayName { get; }
  123: public int MaxFixAttempts { get; }
  126: public StrategyBuildProfile? Profile { get; }
  129: public IReadOnlyList<CodegenMessage> Transcript => _messages;
  132: public IReadOnlyList<StrategyFile> Files { get; private set; } = [];
  135: public CodegenUsage TotalUsage { get; private set; } = CodegenUsage.None;
  147: public async Task<StrategyBuildTurn> SendAsync(
  257: public void SyncEditedFiles(IReadOnlyList<StrategyFile> files) => Files = files;
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenClientFactory.cs
```cs
   19: public sealed class StrategyCodegenClientFactory
   28: public StrategyCodegenClientFactory(Func<HttpClient> httpFactory, AiCodegenOptions options, Func<string, string?> keyResolver)
   38: public IReadOnlyList<IStrategyCodegenClient> BuildAll()
   66: public IStrategyCodegenClient? Build(string providerId, string? model, CodegenEffort effort = CodegenEffort.Default)
   85: public IReadOnlyList<string> ModelsFor(string providerId) =>
   90: public IStrategyCodegenClient? SelectDefault()
```

## src/windows/Tools/DaxAlgo.Codegen/StrategyCodegenOrchestrator.cs
```cs
   10: public sealed record StrategyBuildLoopResult(
   32: public sealed class StrategyCodegenOrchestrator(
   46: public StrategyBuildSession CreateSession(
   58: public async Task<StrategyBuildLoopResult> BuildAsync(
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

## src/windows/Tools/DaxAlgo.Codegen/StrategySkillLibrary.cs
```cs
   11: public sealed record StrategySkill(string Id, string Name, IReadOnlyList<string> Triggers, string Body)
   15: public int Score(string text)
   41: public sealed class StrategySkillLibrary
   46: public const int MaxSkillsPerSession = 3;
   47: public const int MaxCharacters = 12_000;
   53: public IReadOnlyList<StrategySkill> All => _skills;
   57: public static StrategySkillLibrary Load()
   77: public IReadOnlyList<StrategySkill> SelectFor(string? brief) => SelectFor(brief, MaxSkillsPerSession);
   82: public IReadOnlyList<StrategySkill> SelectFor(string? brief, int maxSkills)
  109: public static string Compose(string basePack, IReadOnlyList<StrategySkill> skills)
```
