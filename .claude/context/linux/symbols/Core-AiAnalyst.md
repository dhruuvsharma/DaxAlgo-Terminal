# TradingTerminal.Core / AiAnalyst — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/AiAnalyst/AiAnalystDecision.cs
```cs
    3: public enum AiAnalystDecision
```

## src/linux/Core/TradingTerminal.Core/AiAnalyst/AnalystBar.cs
```cs
    7: public sealed record AnalystBar(
```

## src/linux/Core/TradingTerminal.Core/AiAnalyst/AnalystReport.cs
```cs
    8: public sealed record IndicatorReport(
   16: public sealed record PatternReport(
   25: public sealed record TrendReport(
   38: public sealed record AnalystReport(
   56: public static AnalystReport Unavailable(string reason) => new(
```

## src/linux/Core/TradingTerminal.Core/AiAnalyst/AnalystRequest.cs
```cs
    8: public sealed record AnalystRequest(
```

## src/linux/Core/TradingTerminal.Core/AiAnalyst/IAiAnalystClient.cs
```cs
   12: public interface IAiAnalystClient
   17:     bool IsAvailable { get; }
   19:     Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default);
```
