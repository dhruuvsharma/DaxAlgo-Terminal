# DaxAlgo Terminal — Polyglot Architecture

## The rule

**Other languages live in their own process, behind a subprocess + file/JSON seam. Never P/Invoke, never Python.NET, never embedded interpreters.**

Why: the WPF build must stay hermetic (one `dotnet build`, no native toolchains, no Python venv on the path). The C# side gets one interface per tool; the tool stays in its own toolchain (CMake / pip), its own repo if you want. If the bridge breaks, the terminal degrades gracefully ("Fast backtest unavailable — falling back to managed engine") — it never crashes the shell.

## The two tools (today)

| Tool | Language | Job | Process model | Talks to C# via |
|---|---|---|---|---|
| `tick-backtester` | C++20 | High-throughput tick replay for huge datasets (10M+ ticks) | One-shot subprocess per run | Parquet (input) + JSON (config + result) |
| `daxalgo-ml` | Python 3.11+ | Feature engineering, labelling, model fit/predict | Long-lived local service (FastAPI on 127.0.0.1) | HTTP/JSON, Parquet for bulk data |

One-shot vs long-lived is the key call: backtests are batch (spawn, run, exit), ML is iterative (fit once, predict many times) — keeping the Python process warm avoids reloading sklearn/torch per call.

## Layout

```
DaxAlgo Terminal/
├── src/                                  ← C# only, no changes to its build
│   └── TradingTerminal.Infrastructure/
│       ├── Backtest/Fast/                ← IFastBacktestRunner + ProcessFastBacktestRunner
│       └── Ml/                           ← IPythonMlClient + HttpPythonMlClient + lifecycle
└── tools/                                ← polyglot sidecars, each with its own build
    ├── cpp-backtester/                   ← git submodule pointing at Tick-BackTester
    │   ├── CMakeLists.txt
    │   └── bin/tick_backtester.exe       ← copied to App output via csproj <None Include>
    └── python-ml/
        ├── pyproject.toml
        ├── daxalgo_ml/                   ← FastAPI app + sklearn/pandas wrappers
        └── bin/daxalgo-ml.exe            ← PyInstaller-frozen launcher (no venv on user box)
```

`tools/` is **excluded from the .sln**. Each sidecar builds via its own CI step; the App project's csproj copies the built artefact into the output folder via `<None Include="...\bin\*" CopyToOutputDirectory="PreserveNewest">`.

## C++ backtester seam

```
C#                                            C++
┌───────────────────────────┐                ┌──────────────────────┐
│ FastBacktestRequest       │  JSON stdin →  │ tick_backtester.exe  │
│ - parquet_path            │                │ (reads stdin config, │
│ - strategy_id             │                │  opens parquet,      │
│ - params { ... }          │                │  runs, writes JSON   │
│ - fee_model               │                │  to stdout)          │
│ - risk_caps               │                └──────────┬───────────┘
└───────────────────────────┘                           │
                                                        │ JSON stdout
┌───────────────────────────┐                           │
│ FastBacktestResult        │ ◄─────────────────────────┘
│ - stats { sharpe, ... }   │   shape = StatisticsCalculator's snapshot
│ - equity_curve_parquet    │   bulk data via temp Parquet file
│ - trades_parquet          │
└───────────────────────────┘
```

- **Strategy identity is a string id, not a C# delegate.** The C++ engine has its own strategies (mean-reversion ships today; more get ported as demand appears). The C# `BacktestStrategyCatalog` has a per-entry "fast?: bool" flag — if false, the Fast toggle is greyed out for that strategy.
- **Result shape matches C# `BacktestStats`** verbatim so the same UI renders both. Add fields only by widening both sides together.
- **No streaming.** Backtest is fire-and-forget; progress goes to stderr line-by-line if we want a progress bar.

## Python ML seam

```
App startup           App shutdown
     │                     │
     ▼                     ▼
┌─────────────────────────────────────┐
│ PythonMlLifecycle (singleton)        │
│  - spawns daxalgo-ml.exe on first   │  ← lazy: don't pay the cost if user never opens AI tab
│    use, kills on app exit            │
│  - probes /health, surfaces failure  │
│    as IObservable<MlServiceState>    │
└──────────┬───────────────────────────┘
           │ HTTP localhost:<picked port>
           ▼
┌─────────────────────────────────────┐
│ FastAPI service                      │
│  POST /features/triple-barrier       │  ← batch: Parquet path in, Parquet path out
│  POST /models/{name}/fit             │  ← state lives in service memory
│  POST /models/{name}/predict         │  ← fast: model already loaded
│  GET  /models                        │
│  GET  /health                        │
└─────────────────────────────────────┘
```

- **Localhost only, ephemeral port.** Pick a free port at spawn, pass via env var; never bind to 0.0.0.0.
- **No auth.** Loopback-only + process-bound lifetime = same trust boundary as the WPF app itself.
- **Big data crosses as Parquet paths, not JSON.** A 1M-row feature matrix in JSON would be insane; we already write Parquet for ticks, reuse it.
- **Models persist to disk per session.** `~/.daxalgo/models/<name>.joblib`. The Python service rehydrates on request, doesn't try to keep everything in RAM.

## What this is NOT

- **Not a microservice architecture.** Both sidecars run on the user's machine, bound to this one app's lifetime. No Docker, no service mesh, no auth tokens.
- **Not in-process.** No Python.NET, no `Python.Runtime`, no native DLL P/Invoke into C++ code. Each tool's crash is recoverable; an in-process crash takes the WPF shell down.
- **Not a strategy-portability story.** The C++ engine doesn't run C# strategies. The Python service doesn't run C# code. Each side owns its strategies / models; the contract is on the data shape that crosses the boundary.
- **Not part of the .NET solution.** `tools/` has its own build. The App project just consumes the built binary.

## When to add a third language

You shouldn't, unless the same justification applies: an existing ecosystem you can't reasonably reproduce in the languages already on the team (C#, C++, Python). Rust for an order-book matcher? Maybe. Go for a market-data proxy? Probably not — C# already does that. Every new language is a new toolchain on every developer's box and a new way for the build to break.

## Migration order

1. **Subprocess plumbing in C# first** — `IFastBacktestRunner` + `IPythonMlClient` interfaces, with managed `Null*` implementations that throw "not configured." This unblocks the UI work.
2. **Python sidecar second** — bigger leverage (no Python ML in the app today), smaller surface (FastAPI + a couple of endpoints).
3. **C++ bridge third** — only worth it once a user actually hits a dataset the C# engine struggles with. The C# engine handles 1M ticks comfortably; this seam is for the day someone wants 50M.
