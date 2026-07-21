# TradingTerminal.Infrastructure / Sidecar — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Sidecar/JobObjectProcessGuard.cs
```cs
   18: public bool IsConfigured =>
   22: public JobObjectProcessGuard()
   57: public bool TryAssign(Process process)
   66: public void Dispose() => Close();
  102: public long PerProcessUserTimeLimit;
  103: public long PerJobUserTimeLimit;
  104: public uint LimitFlags;
  105: public UIntPtr MinimumWorkingSetSize;
  106: public UIntPtr MaximumWorkingSetSize;
  107: public uint ActiveProcessLimit;
  108: public UIntPtr Affinity;
  109: public uint PriorityClass;
  110: public uint SchedulingClass;
  116: public ulong ReadOperationCount;
  117: public ulong WriteOperationCount;
  118: public ulong OtherOperationCount;
  119: public ulong ReadTransferCount;
  120: public ulong WriteTransferCount;
  121: public ulong OtherTransferCount;
  127: public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
  128: public IO_COUNTERS IoInfo;
  129: public UIntPtr ProcessMemoryLimit;
  130: public UIntPtr JobMemoryLimit;
  131: public UIntPtr PeakProcessMemoryUsed;
  132: public UIntPtr PeakJobMemoryUsed;
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
