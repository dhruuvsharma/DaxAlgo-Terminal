# TradingTerminal.Backtest.Client — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Client/AbandonedWorkerStagingCleaner.cs
```cs
   13: public static int Cleanup(string jobRoot, TimeSpan minimumAge, DateTime utcNow)
```

## src/windows/Backtest/TradingTerminal.Backtest.Client/BacktestJobClient.cs
```cs
   22: public sealed class BacktestJobClient : IBacktestJobClient, IDisposable
   30: public BacktestJobClient(
   37: public BacktestJobClient(
   45: public async Task<BacktestJobOutcome> RunAsync(
  480: public void Dispose()
 1292: public Process Process { get; } = process;
 1294: public bool TryAssign() => guard.TryAssign(Process);
 1296: public void Dispose()
```

## src/windows/Backtest/TradingTerminal.Backtest.Client/BacktestWorkerExecutableResolver.cs
```cs
   15: public static bool TryResolve(
```

## src/windows/Backtest/TradingTerminal.Backtest.Client/BacktestWorkerOptions.cs
```cs
    5: public sealed class BacktestWorkerOptions
   11: public string? WorkerExecutablePath { get; set; }
   14: public List<string> WorkerArguments { get; } = [];
   17: public string? JobRootDirectory { get; set; }
   20: public string? StrategyBundleStoreRoot { get; set; }
   26: public StrategyBundleInstallPolicy? StrategyBundlePolicy { get; set; }
   29: public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);
   31: public int ProgressBufferCapacity { get; set; } = 32;
   32: public int MaxProgressLineCharacters { get; set; } = 16 * 1024;
   33: public int MaxCapturedStandardErrorCharacters { get; set; } = 64 * 1024;
   36: public TimeSpan AbandonedStagingAge { get; set; } = TimeSpan.FromDays(2);
```

## src/windows/Backtest/TradingTerminal.Backtest.Client/BacktestWorkerServiceCollectionExtensions.cs
```cs
    5: public static class BacktestWorkerServiceCollectionExtensions
    7: public static IServiceCollection AddBacktestWorker(
```

## src/windows/Backtest/TradingTerminal.Backtest.Client/IBacktestJobClient.cs
```cs
    6: public interface IBacktestJobClient
    8:     Task<BacktestJobOutcome> RunAsync(
    9:     BacktestJobRequest request,
   10:     IProgress<BacktestJobProgress>? progress = null,
   11:     CancellationToken ct = default);
```
