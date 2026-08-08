# DaxSandboxVisualizer sandbox contract

This project was created by `dotnet new daxalgo-sandbox-visualizer`. It targets `DaxAlgo.Sdk`
`0.3.0-alpha`; the package supplies the `DAX3001` sandbox analyzer automatically. Keep the project
analyzer-clean.

## Unit contract

- Implement `IVisualizer`: declare `Schema` and `DataRequirement`, then handle only the lifecycle
  callbacks the visualizer needs. The host starts it automatically.
- Read only `context.Data`, `context.Clock`, and `context.Parameters`.
- Keep computed presentation data in this visualizer's own view-state. `IVisualizerContext` has no
  `Book`; a visualizer cannot emit a target or trade.
- Send bounded messages only through `context.Alerts`. The host owns the Activity Log/banner routes,
  rate limits, and deduplication.
- Put all run state on the visualizer instance. Use `context.Clock` for deterministic time. The host
  builds parameter UI from `StrategyParameterSchema`; do not add host wiring.

## Forbidden source references

`DAX3001` rejects the entire project when any source type implements `IVisualizer` and source code
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
configuration, local state for the view, and `IAlertSink` for messages. `Task`, `async`/`await`,
`Task.Run`, `SemaphoreSlim`, LINQ, `Math`, `Span<T>`, `stackalloc`, and collection expressions are
allowed. Compiler lowering may emit `MemoryMarshal`; author-written interop remains forbidden.

## Workflow

Change the visualizer and add a direct unit test using public interface test doubles. Once SDK
`0.3.0-alpha` is published, run `dotnet test DaxSandboxVisualizer.slnx`. DaxAlgo Terminal auto-runs
the visualizer in the product; it has no account, backtest, broker, or execution capability.
