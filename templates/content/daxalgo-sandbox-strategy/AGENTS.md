# DaxSandboxStrategy sandbox contract

This project was created by `dotnet new daxalgo-sandbox-strategy`. It targets `DaxAlgo.Sdk`
`0.3.0`; the package supplies the `DAX3001` sandbox analyzer automatically. Keep the project
analyzer-clean.

## Unit contract

- Implement `IStrategyKernel`: declare `Schema` and `DataRequirement`, then handle only the lifecycle
  callbacks the strategy needs.
- Read only `context.Data`, `context.Clock`, and `context.Parameters`.
- The only trading output is `context.Book.SetTargetPosition(...)`: signed reference units, with zero
  meaning flat and optional model-book stop/target prices. Never place or model a broker order.
- Send bounded messages only through `context.Alerts`. The host owns the Activity Log/banner routes,
  rate limits, and deduplication.
- Put all run state on the kernel instance. Use `context.Clock` for deterministic time. The host builds
  parameter UI from `StrategyParameterSchema`; do not add UI or host wiring.

## Forbidden source references

`DAX3001` rejects the entire project when any source type implements `IStrategyKernel` and source code
uses:

- `System.IO`; `System.Net*`; `System.Diagnostics.Process`/`ProcessStartInfo`;
- `System.Reflection.Emit`, `System.Reflection.Assembly.Load*`, or
  `System.Runtime.Loader.AssemblyLoadContext`;
- any `System.Runtime.InteropServices` API (including `Marshal` and `MemoryMarshal`) or P/Invoke;
- `System.Environment`, `System.AppDomain`, or `Microsoft.Win32` registry APIs;
- raw threading/OS-wait types: `Thread`, `ThreadPool`, `System.Threading.Timer`, `PeriodicTimer`,
  `Mutex`, `Semaphore`, wait handles/events, `RegisteredWaitHandle`, and overlapped-I/O types;
- terminal host services/namespaces, broker clients, market-data hubs/stores, repositories, execution,
  backtest, UI, login, settings, or recording APIs.

Use the supplied context instead: `IMarketDataView` for data, `IClock` for time, `IParameters` for
configuration, `IVirtualBook` for targets, and `IAlertSink` for messages. `Task`, `async`/`await`,
`Task.Run`, `SemaphoreSlim`, LINQ, `Math`, `Span<T>`, `stackalloc`, and collection expressions are
allowed. Compiler lowering may emit `MemoryMarshal`; author-written interop remains forbidden.

## Workflow

Change the kernel and add a direct unit test using public interface test doubles. Once SDK
`0.3.0` is published, run `dotnet test DaxSandboxStrategy.slnx`. Product backtests and live
execution run inside DaxAlgo Terminal; this scaffold does not contain an account, backtest runner,
broker, or execution engine.
