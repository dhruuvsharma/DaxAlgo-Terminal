# TradingTerminal.Infrastructure / Strategies — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/AgentCliCodegenClient.cs
```cs
    9: public sealed record AgentCliAdapter(
   17: public static AgentCliAdapter ClaudeCode { get; } =
   22: public static AgentCliAdapter Codex { get; } =
   25: public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];
   36: public sealed class AgentCliCodegenClient : IStrategyCodegenClient
   42: public AgentCliCodegenClient(AgentCliAdapter adapter, Func<string, string?>? resolveOnPath = null, TimeSpan? timeout = null)
   49: public string ProviderId => _adapter.ProviderId;
   50: public string DisplayName => _adapter.DisplayName;
   51: public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;
   53: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/AnthropicCodegenClient.cs
```cs
   14: public sealed class AnthropicCodegenClient : IStrategyCodegenClient
   24: public AnthropicCodegenClient(HttpClient http, string baseUrl, string model, string? apiKey)
   32: public string ProviderId => "anthropic";
   33: public string DisplayName => "Anthropic (API key)";
   34: public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
   36: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/CodegenCodeExtractor.cs
```cs
    8: public static partial class CodegenCodeExtractor
   13: public static string Extract(string? reply)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/FakeCodegenClient.cs
```cs
   11: public sealed class FakeCodegenClient : IStrategyCodegenClient
   18: public FakeCodegenClient(params string[] replies)
   23: public string ProviderId => "fake";
   24: public string DisplayName => "Fake (deterministic)";
   25: public bool IsAvailable => true;
   28: public int CallCount { get; private set; }
   30: public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
   40: public const string DefaultKernel = """
   42: public sealed class GeneratedStrategy(Contract contract) : IBacktestStrategy
   48: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   50: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
   59: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
   60: public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/OpenAiCompatibleCodegenClient.cs
```cs
   15: public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
   28: public OpenAiCompatibleCodegenClient(
   43: public string ProviderId { get; }
   44: public string DisplayName { get; }
   46: public bool IsAvailable =>
   50: public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/RoslynStrategyCompiler.cs
```cs
   24: public sealed class RoslynStrategyCompiler : IStrategyCompiler
   45: public StrategyCompileResult Compile(StrategyScript script)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/StrategyCodegenClientFactory.cs
```cs
   15: public sealed class StrategyCodegenClientFactory
   24: public StrategyCodegenClientFactory(Func<HttpClient> httpFactory, AiCodegenOptions options, Func<string, string?> keyResolver)
   34: public IReadOnlyList<IStrategyCodegenClient> BuildAll()
   61: public IStrategyCodegenClient? SelectDefault()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/StrategyCodegenOrchestrator.cs
```cs
   10: public sealed record StrategyBuildLoopResult(
   30: public sealed class StrategyCodegenOrchestrator(IStrategyCompiler compiler, ILogger<StrategyCodegenOrchestrator>? logger = null)
   35: public async Task<StrategyBuildLoopResult> BuildAsync(
```
