# TradingTerminal.Infrastructure / Sidecar — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Sidecar/JobObjectProcessGuard.cs
```cs
   17: public JobObjectProcessGuard()
   48: public bool TryAssign(Process process)
   55: public void Dispose()
   87: public long PerProcessUserTimeLimit;
   88: public long PerJobUserTimeLimit;
   89: public uint LimitFlags;
   90: public UIntPtr MinimumWorkingSetSize;
   91: public UIntPtr MaximumWorkingSetSize;
   92: public uint ActiveProcessLimit;
   93: public UIntPtr Affinity;
   94: public uint PriorityClass;
   95: public uint SchedulingClass;
  101: public ulong ReadOperationCount;
  102: public ulong WriteOperationCount;
  103: public ulong OtherOperationCount;
  104: public ulong ReadTransferCount;
  105: public ulong WriteTransferCount;
  106: public ulong OtherTransferCount;
  112: public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
  113: public IO_COUNTERS IoInfo;
  114: public UIntPtr ProcessMemoryLimit;
  115: public UIntPtr JobMemoryLimit;
  116: public UIntPtr PeakProcessMemoryUsed;
  117: public UIntPtr PeakJobMemoryUsed;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Sidecar/SidecarHostService.cs
```cs
   39: public SidecarHostService(
   51: public bool IsRunning { get; private set; }
   59: public Task StartAsync(CancellationToken cancellationToken)
   70: public Task StopAsync(CancellationToken cancellationToken)
   76: public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
  288: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Sidecar/SidecarServiceCollectionExtensions.cs
```cs
    9: public static class SidecarServiceCollectionExtensions
   17: public static IServiceCollection AddSidecar(this IServiceCollection services, IConfiguration configuration)
```
