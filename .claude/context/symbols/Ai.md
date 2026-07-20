# TradingTerminal.Ai — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/AI/TradingTerminal.Ai/Analyst/AiAnalystEnricher.cs
```cs
   29: public AiAnalystEnricher(
   43: public bool ShouldRun(StrategyNotification notification)
   53: public async Task<StrategyNotification> EnrichAsync(StrategyNotification notification, CancellationToken ct)
```

## src/windows/AI/TradingTerminal.Ai/Analyst/AiAnalystServiceCollectionExtensions.cs
```cs
   11: public static class AiAnalystServiceCollectionExtensions
   19: public static IServiceCollection AddAiAnalyst(this IServiceCollection services, IConfiguration configuration)
   46: public DispatchingAiAnalystClient(
   56: public bool IsAvailable => _options.CurrentValue.AiAnalyst.Enabled;
   58: public Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default) =>
```

## src/windows/AI/TradingTerminal.Ai/Analyst/HttpAiAnalystClient.cs
```cs
   20: public const string HttpClientName = "ai-analyst";
   33: public HttpAiAnalystClient(
   43: public bool IsAvailable => _options.CurrentValue.AiAnalyst.Enabled;
   45: public async Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default)
  128: public AnalystReport ToReport() => new(
```

## src/windows/AI/TradingTerminal.Ai/Analyst/NullAiAnalystClient.cs
```cs
   12: public bool IsAvailable => false;
   14: public Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default) =>
```
