# Sandboxed strategy and visualizer authoring

This is the authoritative implementation contract for humans and authoring agents that create
`IStrategyKernel` or `IVisualizer` units against `DaxAlgo.Sdk`. It describes the public SDK surface,
the source and load-time policy, the open-core testing boundary, and the product runtime boundary.

## 1. What a sandboxed unit is, and why

A sandboxed unit is pure, capability-scoped computation:

```text
scoped market data + deterministic clock + typed parameters
    -> strategy/visualizer math
    -> virtual-book targets or local view-state + mediated alerts
```

The host gives a unit only the capabilities required for that flow. A unit does not receive a broker,
account, market-data hub or store, file system, network, process, registry, execution engine, replay
engine, or terminal service. The terminal therefore never has to trust authored code with more
authority than its job requires.

The boundary has three shipped layers:

1. The `DaxAlgo.Sdk.Analyzers.ForbiddenApiAnalyzer` emits build error `DAX3001` for forbidden source
   references. The analyzer is embedded in the `DaxAlgo.Sdk` NuGet package and activates across the
   compilation when a source type implements `IStrategyKernel` or `IVisualizer`.
2. `PluginPolicyScanner` applies its `Sandbox` profile to compiled IL before load. Manifest permissions
   cannot relax this profile.
3. Even accepted code receives only the narrow context described below.

These controls reduce authority; they are not a claim that an in-process IL scan is a complete hostile-
code security boundary. Product isolation and execution gates remain host-owned.

## 2. Exact capability contexts

### Strategy: `IStrategyRuntimeContext`

| Member | Contract |
|---|---|
| `IMarketDataView Data` | Read-only projection for the declared instrument set and `DataRequirement`. `RecentBars`, `RecentQuotes`, and `RecentTrades` return bounded snapshots ordered oldest to newest; `LatestDepth` returns the latest authorized snapshot or `null`. Requests outside the declared set/stream return no data. There is no hub, store, broker feed, or source selector. |
| `IClock Clock` | Host clock. It is live time in a live run and replay time in a historical run. Use it for every time-dependent decision instead of wall-clock calls. |
| `IParameters Parameters` | Read-only, case-sensitive values governed by the unit's `StrategyParameterSchema`. Read with `GetInt`, `GetLong`, `GetDouble`, `GetBool`, `GetString`, `GetText`, `GetEnum<TEnum>`, or `GetInstrument` according to the declared kind. |
| `IVirtualBook Book` | The strategy's sole trading output: declarative targets in its private model portfolio. It cannot inspect or manipulate real orders, brokers, venues, routes, or accounts. |
| `IAlertSink Alerts` | Bounded message offer to host-owned Activity Log and banner routes. It does not expose a destination, recipient, transport, or notification service. |

### Visualizer: `IVisualizerContext`

`IVisualizerContext` contains the same `Data`, `Clock`, `Parameters`, and `Alerts` members. It
deliberately has no `Book`. A visualizer may update state that belongs to its own view and offer alerts;
it cannot produce a target or any trading output.

Declare only the streams actually consumed with `StrategyDataRequirement`: `Bars`, `L1`, `Depth`, and
`TradeTape` are flags and may be combined. The host intersects those requirements with the selected
instrument set and available feed capabilities.

## 3. Entry contracts and event lifecycle

Both entry contracts declare:

- `StrategyParameterSchema Schema`: complete launch-time parameter metadata;
- `StrategyDataRequirement DataRequirement`: required market-data categories;
- `OnStartAsync(...)`: initialization of a fresh instance;
- `OnQuoteAsync(...)`, `OnTradeAsync(...)`, `OnDepthAsync(...)`, and `OnBarAsync(...)`: callbacks for
  authorized events; and
- `OnStopAsync(...)`: teardown before that instance is discarded.

All callbacks receive the appropriate context and a `CancellationToken`. The quote, trade, and bar
records carry their canonical `InstrumentId`; the depth callback supplies the instrument explicitly.
The four event handlers and `OnStopAsync` have no-op defaults, so implement only those the declared
data requirement needs. `OnStartAsync` is required.

An `IStrategyKernel` starts when the product run starts. An `IVisualizer` has the same event shape but
auto-runs when hosted; it has no separate Run command and no trading surface. Keep mutable run state on
the instance. A resumed unit is a fresh instance, so never rely on static mutable state to survive a
pause/rebuild.

## 4. The virtual book

`IVirtualBook` accepts a `VirtualTargetIntent`, normally through:

```csharp
context.Book.SetTargetPosition(
    instrument,
    targetUnits,
    protectiveStopPrice: optionalStop,
    profitTargetPrice: optionalTarget);
```

`targetUnits` is a signed desired position in reference units: positive is long, negative is short,
and zero is flat. The optional stop and target are model-book prices. The call describes the desired
portfolio state; it is not an order and cannot name a broker, order type, time-in-force, account, venue,
or execution route.

The host-owned model portfolio is used for every strategy run. The kernel always emits the
same declarative target; it never creates or routes a broker order. Any future replication, sizing,
risk, broker translation, or execution authorization remains outside the kernel and outside this
public application's current data-and-signals-only boundary.

## 5. Parameter schema, defaults, run, and pause

Declare every editable value in `Schema` with `StrategyParameter.Int`, `Number`, `Bool`, `Choice`,
`Enum`, `Instrument`, or `Text`. A key is stable, ordinal, case-sensitive machine identity; display
name, range, step, choices, group, unit, and description are host-rendered metadata. A unit with no
parameters uses `StrategyParameterSchema.Empty`.

The host creates the parameter form and seeds values from each declaration's default. The author does
not write parameter UI, parse strings, or retain a mutable host value bag. Read the current validated
value through `context.Parameters` during callbacks. An instrument parameter resolves the canonical
instrument set visible through `Data`.

Parameters are locked while callbacks run. Pause completes the active event pump, then permits edits.
Resume snapshots the selected values, stops and disposes the old instance/context, constructs a fresh
unit with the same schema, calls `OnStartAsync`, and starts a new event pump. Instance view/model state
therefore resets on Resume. A replacement whose schema changed is rejected rather than silently
reinterpreting existing values.

## 6. Alerts

Use `context.Alerts.Alert(message, level, dedupeKey)` or `AlertIf(...)`. Levels are `Information`,
`Warning`, `Error`, and `Critical`.

- Messages are limited to `AlertLimits.MaxMessageLength` (512 UTF-16 code units).
- Deduplication keys are limited to `AlertLimits.MaxDedupeKeyLength` (128 UTF-16 code units).
- A non-empty key lets the host coalesce equivalent alerts within its window.
- The sink may throttle accepted alerts. The public `MediatedAlertSink` defaults to 20 alerts per
  10-second window; the host may configure a different positive limit/window.
- Accepted alerts go to the fixed Activity Log and in-view banner routes. Authored code cannot choose
  email, chat, webhook, recipient, device, or any other destination.

Use a stable semantic dedupe key for a condition, not a random value. Alerts are observability/UI
output, never an execution command.

## 7. Rules and forbidden APIs

The source policy below is exact for `ForbiddenApiAnalyzer`. It is compilation-wide once any source
type implements `IStrategyKernel` or `IVisualizer`, so moving a forbidden call into a helper in the
same project does not evade it.

| Forbidden source reference | Use instead |
|---|---|
| Entire `System.IO` namespace | Read authorized, retained market data through `IMarketDataView`. There is no sandbox file capability. |
| Entire `System.Net` namespace and descendants | Use `IMarketDataView` for host-fed data and `IAlertSink` for bounded user-visible messages. There is no outbound network capability. |
| `System.Diagnostics.Process` and `ProcessStartInfo` | Do the calculation in the unit. A unit cannot launch or inspect processes. |
| Entire `System.Reflection.Emit` namespace | Compile the unit ahead of time. Runtime code generation is unavailable. |
| Every `System.Reflection.Assembly.Load*` call and `System.Runtime.Loader.AssemblyLoadContext` | Reference allowed libraries at build time. Runtime assembly loading is unavailable. |
| Entire `System.Runtime.InteropServices` namespace, including `Marshal`, `MemoryMarshal`, native-library APIs, attributes used for P/Invoke, and author-written interop | Use managed math and SDK DTOs. There is no native interop capability. |
| `System.Environment` | Use `context.Clock`, typed parameters, and host callbacks. Environment variables, process state, command lines, and host paths are unavailable. |
| Entire `Microsoft.Win32` namespace | Express configuration in `StrategyParameterSchema`; registry and host configuration are unavailable. |
| `System.AppDomain` | Use ordinary instance-local managed code. Host/runtime-domain controls are unavailable. |
| Raw threading and OS waits: `System.Threading.Thread`, `ThreadPool`, `Timer`, `PeriodicTimer`, `Mutex`, `Semaphore`, `WaitHandle`, `EventWaitHandle`, `AutoResetEvent`, `ManualResetEvent`, `RegisteredWaitHandle`, `Overlapped`, `NativeOverlapped`, and `PreAllocatedOverlapped` | Use async callbacks, `Task`, `Task.Run`, and `SemaphoreSlim` when coordination is genuinely needed. Use `context.Clock` and market-event callbacks for scheduling decisions. |

The analyzer also rejects direct host access:

- `IBrokerClient`, `IMarketDataHub`, `IMarketDataIngest`, `IMarketDataStore`, `InstrumentDataView`, and
  `IQuestDbLauncher`;
- `TradingTerminal.Infrastructure`, `.MarketData`, `.Backtest`, `.App`, `.Execution`, `.UI`, `.Login`,
  `.Settings`, and `.Recording` namespaces;
- `TradingTerminal.Core.Brokers`, `.Trading`, and `.Backtest`; and
- host types under `TradingTerminal` whose names end in `Store`, `Repository`, `BrokerClient`, or
  `BrokerSelector`.

Use `IMarketDataView`, `IClock`, `IParameters`, `IVirtualBook` (strategy only), local visualizer
view-state, and `IAlertSink` instead.

The following are allowed: `Task`, `async`/`await`, `Task.Run`, `SemaphoreSlim`, LINQ, `System.Math`,
`Span<T>`, `stackalloc`, and collection expressions. The C# compiler may lower ordinary async/span/
collection code to memory-safe `System.Runtime.InteropServices.MemoryMarshal` IL. The Sandbox
load-time scanner deliberately tolerates that compiler-generated helper while still blocking P/Invoke
metadata and escaping interop types such as `Marshal`, `NativeLibrary`, and `GCHandle`.

That tolerance is not author permission: do **not** reference `MemoryMarshal` or any other
`System.Runtime.InteropServices` API in source. The source analyzer will reject it.

The load-time Sandbox profile also blocks file/network namespaces, process/environment/AppDomain
access, registry, reflection emit, assembly loading, raw threading/waits, P/Invoke, and the host-service
types/namespaces above. Sandbox manifest permissions are ignored.

## 8. Open-core authoring and product boundary

Author and unit-test the kernel or visualizer against the free, public `DaxAlgo.Sdk`. Public test seams
include `ScopedMarketDataView`, `SandboxParameters`, and `MediatedAlertSink`; a strategy test supplies a
trivial recording `IVirtualBook` and composes `IStrategyRuntimeContext` from those public capabilities.
The public `SandboxVisualizerRuntime` can run a visualizer end-to-end against an in-memory market-data
hub.

The public tree does **not** provide a strategy account or `SandboxStrategyRuntime`. Do not reference
them from an open-core sample or test. Product launch, historical replay, and model portfolios are
host-owned. The public application has no live broker-order
execution path. A visualizer auto-runs inside a compatible host and never participates in execution.

Create a project with either public template:

```powershell
dotnet new install DaxAlgo.Templates::0.3.0 --force

dotnet new daxalgo-sandbox-strategy -n MySandboxStrategy -o "$env:TEMP/MySandboxStrategy"
dotnet build "$env:TEMP/MySandboxStrategy/DaxSandboxStrategy.slnx" -c Release
dotnet test "$env:TEMP/MySandboxStrategy/DaxSandboxStrategy.slnx" -c Release --no-build

dotnet new daxalgo-sandbox-visualizer -n MySandboxVisualizer -o "$env:TEMP/MySandboxVisualizer"
dotnet build "$env:TEMP/MySandboxVisualizer/DaxSandboxVisualizer.slnx" -c Release
dotnet test "$env:TEMP/MySandboxVisualizer/DaxSandboxVisualizer.slnx" -c Release --no-build
```

Both templates reference `DaxAlgo.Sdk` `0.3.0` through NuGet and include a direct unit test plus
`AGENTS.md` and `CLAUDE.md`. SDK and template version `0.3.0` are published on NuGet, and the repository's
template-smoke workflow scaffolds, builds, and tests both templates against the matching package chain.
When developing this repository itself, the two template source directories under `templates/content/`
can also be installed directly. Do not replace the package reference with private product projects.

## 9. Worked sample

The verified public sample is at
[`samples/DaxAlgo.Sandbox.Samples/`](../samples/DaxAlgo.Sandbox.Samples/). Its main project references
the SDK project directly and attaches the shipped analyzer project, so it proves the code against the
current checkout independently of NuGet package resolution.

### `MovingAverageCrossKernel`

[`MovingAverageCrossKernel.cs`](../samples/DaxAlgo.Sandbox.Samples/MovingAverageCrossKernel.cs) is a
single-instrument `IStrategyKernel`:

1. Its schema declares `instrument`, `fastPeriod`, `slowPeriod`, `useProtectiveStop`, and
   `protectiveStopPercent`; its requirement is final one-minute bars.
2. On each authorized final bar, it asks `context.Data.RecentBars` for the slow window and computes the
   fast and slow simple moving averages using only sandbox data and math.
3. The first valid fast/slow relation arms its instance state without emitting a false startup cross.
4. A bearish-to-bullish cross submits `+1` reference unit. When enabled, the protective model stop is
   `close * (1 - protectiveStopPercent / 100)`.
5. A bullish-to-bearish cross submits `0` with no stop, declaring the model portfolio flat.
6. Each direction change offers a deduplicated mediated alert. The kernel never names an order,
   broker, account, or execution route.

The tests under
[`DaxAlgo.Sandbox.Samples.Tests/`](../samples/DaxAlgo.Sandbox.Samples/DaxAlgo.Sandbox.Samples.Tests/)
direct-drive `OnBarAsync`. Their public test context combines `ScopedMarketDataView` fed by a fixed
in-memory hub, `SandboxParameters`, a recording `IVirtualBook`, and `MediatedAlertSink`. They assert the
expected long and flat target intents, protective-stop value, and alert delivery. No Pro account or
host runtime is involved.

### `SpreadBandVisualizer`

[`SpreadBandVisualizer.cs`](../samples/DaxAlgo.Sandbox.Samples/SpreadBandVisualizer.cs) is an
`IVisualizer` with `instrument`, `lookback`, and `bandMultiplier` parameters and `Bars | L1` data:

1. It reads recent final one-minute closes and computes their mean and population standard deviation.
2. Its rolling band is `mean +/- bandMultiplier * populationStandardDeviation`.
3. It evaluates a final bar close or the latest quote midpoint, then updates its public view-state with
   the center, bounds, price, and inside/outside status.
4. It alerts only on an inside-to-outside transition, avoiding repeated alerts while price remains
   outside. It has no book and emits no target.

The visualizer test uses the public `SandboxVisualizerRuntime`, feeds authorized events through an
in-memory hub, and asserts view-state and mediated alert behavior.

[`ForbiddenApis.cs.txt`](../samples/DaxAlgo.Sandbox.Samples/ForbiddenApis.cs.txt) is excluded from
compilation and shows negative source examples for file, process, and interop access. They are examples
of code `DAX3001` rejects; the real sample remains analyzer-clean and buildable.

## 10. For Hyperion: machine contract

Hyperion and other generators must treat this JSON as a generation constraint, not a suggestion:

```json
{
  "contract": "daxalgo-sandbox-authoring/1",
  "sdkPackage": "DaxAlgo.Sdk",
  "sdkVersion": "0.3.0",
  "unitKinds": {
    "strategy": {
      "implements": "DaxAlgo.Sdk.IStrategyKernel",
      "context": "DaxAlgo.Sdk.IStrategyRuntimeContext",
      "inputs": ["Data", "Clock", "Parameters"],
      "outputs": ["Book.SetTargetPosition", "Alerts.Alert", "Alerts.AlertIf"],
      "bookUnits": "signed reference units; zero is flat",
      "optionalIntentFields": ["protectiveStopPrice", "profitTargetPrice"]
    },
    "visualizer": {
      "implements": "DaxAlgo.Sdk.IVisualizer",
      "context": "DaxAlgo.Sdk.IVisualizerContext",
      "inputs": ["Data", "Clock", "Parameters"],
      "outputs": ["own view-state", "Alerts.Alert", "Alerts.AlertIf"],
      "bookAvailable": false,
      "autoRuns": true
    }
  },
  "requiredDeclarations": ["Schema", "DataRequirement", "OnStartAsync"],
  "optionalCallbacks": ["OnQuoteAsync", "OnTradeAsync", "OnDepthAsync", "OnBarAsync", "OnStopAsync"],
  "forbiddenSourceNamespacePrefixes": [
    "System.IO",
    "System.Net",
    "System.Reflection.Emit",
    "System.Runtime.InteropServices",
    "Microsoft.Win32",
    "TradingTerminal.Infrastructure",
    "TradingTerminal.MarketData",
    "TradingTerminal.Backtest",
    "TradingTerminal.App",
    "TradingTerminal.Execution",
    "TradingTerminal.UI",
    "TradingTerminal.Login",
    "TradingTerminal.Settings",
    "TradingTerminal.Recording",
    "TradingTerminal.Core.Brokers",
    "TradingTerminal.Core.Trading",
    "TradingTerminal.Core.Backtest"
  ],
  "forbiddenSourceTypesOrMembers": [
    "System.Diagnostics.Process",
    "System.Diagnostics.ProcessStartInfo",
    "System.Reflection.Assembly.Load*",
    "System.Runtime.Loader.AssemblyLoadContext",
    "System.Environment",
    "System.AppDomain",
    "System.Threading.Thread",
    "System.Threading.ThreadPool",
    "System.Threading.Timer",
    "System.Threading.PeriodicTimer",
    "System.Threading.Mutex",
    "System.Threading.Semaphore",
    "System.Threading.WaitHandle",
    "System.Threading.EventWaitHandle",
    "System.Threading.AutoResetEvent",
    "System.Threading.ManualResetEvent",
    "System.Threading.RegisteredWaitHandle",
    "System.Threading.Overlapped",
    "System.Threading.NativeOverlapped",
    "System.Threading.PreAllocatedOverlapped",
    "TradingTerminal.Core.MarketData.IBrokerClient",
    "TradingTerminal.Core.MarketData.IMarketDataHub",
    "TradingTerminal.Core.MarketData.IMarketDataIngest",
    "TradingTerminal.Core.MarketData.IMarketDataStore",
    "TradingTerminal.Core.MarketData.InstrumentDataView",
    "TradingTerminal.Core.MarketData.IQuestDbLauncher"
  ],
  "forbiddenTradingTerminalTypeNameSuffixes": [
    "Store",
    "Repository",
    "BrokerClient",
    "BrokerSelector"
  ],
  "allowed": [
    "Task",
    "async/await",
    "Task.Run",
    "SemaphoreSlim",
    "LINQ",
    "System.Math",
    "Span<T>",
    "stackalloc",
    "collection expressions"
  ],
  "authorWrittenMemoryMarshalAllowed": false,
  "compilerGeneratedMemoryMarshalToleratedByIlScanner": true,
  "generationRules": [
    "Use context.Clock for time-dependent logic",
    "Keep mutable run state on the unit instance",
    "Declare every parameter and default in StrategyParameterSchema",
    "Request only consumed StrategyDataRequirement flags",
    "Generate no UI, broker, account, store, network, file, process, replay, or execution wiring",
    "Generate at least one direct public-interface unit test",
    "Require an analyzer-clean build before acceptance"
  ],
  "publicBoundary": "author and unit-test against public DaxAlgo.Sdk; public runtime hosting exists for visualizers",
  "terminalBoundary": "run strategies and auto-run visualizers inside a compatible DaxAlgo host; no public live broker-order execution path"
}
```

## Design rationale

This public guide is the self-contained implementation and authoring contract. Private product design
records are intentionally not required to understand or use the public SDK.
