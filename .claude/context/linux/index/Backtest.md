# Linux index / Backtest

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/Backtest/TradingTerminal.Backtest.Cli/Args.cs` | 64 | linux | TradingTerminal.Backtest.Cli | product | Y | Tiny CLI parser: supports --name value and --name=value. No NuGet dependency |
| `src/linux/Backtest/TradingTerminal.Backtest.Cli/Output/ResultWriter.cs` | 67 | linux | TradingTerminal.Backtest.Cli | product | Y | Writes a finished to disk: summary.json (stats + |
| `src/linux/Backtest/TradingTerminal.Backtest.Cli/Program.cs` | 723 | linux | TradingTerminal.Backtest.Cli | product | N |  |
| `src/linux/Backtest/TradingTerminal.Backtest.Cli/StoreFactory.cs` | 89 | linux | TradingTerminal.Backtest.Cli | product | Y | Look up the canonical id for a symbol by querying the |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Accounting/Portfolio.cs` | 152 | linux | TradingTerminal.Backtest.Engine | product | Y | Update the latest mark for an instrument and the open lots' favorable/adverse |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/BacktestEngine.cs` | 130 | linux | TradingTerminal.Backtest.Engine | product | Y | Drives one backtest end-to-end. Single-threaded by design: there is one logical timeline |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Cost/FeeModels.cs` | 16 | linux | TradingTerminal.Backtest.Engine | product | Y | Maps the serializable |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Execution/EngineOrderRouter.cs` | 47 | linux | TradingTerminal.Backtest.Engine | product | Y | The kernel-facing order seam for the backtester. Resolves each 's |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Execution/IFillModel.cs` | 73 | linux | TradingTerminal.Backtest.Engine | product | Y | Decides whether a working order fills against the current quote and at |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Execution/SimulatedOrderBook.cs` | 94 | linux | TradingTerminal.Backtest.Engine | product | Y | Raised for every order transition: the instrument the order trades, then the |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Execution/WorkingOrder.cs` | 19 | linux | TradingTerminal.Backtest.Engine | product | Y | A live order resting in the simulated book, tagged with the instrument |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/AsyncMerge.cs` | 49 | linux | TradingTerminal.Backtest.Engine | product | Y | K-way merge of already-ascending streams into one globally |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/IMarketDataFeed.cs` | 14 | linux | TradingTerminal.Backtest.Engine | product | Y | Produces the time-ordered event stream the engine replays for a run. Implementations |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/InMemoryMarketDataFeed.cs` | 28 | linux | TradingTerminal.Backtest.Engine | product | Y | A feed backed by an in-memory event list — the workhorse for |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/StoreMarketDataFeed.cs` | 50 | linux | TradingTerminal.Backtest.Engine | product | Y | Replays a run from the canonical market-data store — the primary data |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticMarketDataFeed.cs` | 48 | linux | TradingTerminal.Backtest.Engine | product | Y | A deterministic synthetic feed: a mean-reverting (Ornstein-Uhlenbeck-ish) random walk of the mid |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticTapeFeed.cs` | 95 | linux | TradingTerminal.Backtest.Engine | product | Y | Default anchor: a weekday in the London/NY overlap so session gates pass. |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Kernels/BacktestStrategyKernelAdapter.cs` | 50 | linux | TradingTerminal.Backtest.Engine | product | Y | Wrap an already-built legacy strategy (used by the parity test). |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Kernels/MeanReversionKernel.cs` | 81 | linux | TradingTerminal.Backtest.Engine | product | Y | Catalog descriptor — its tunable surface for the Studio, the optimizer, and |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Kernels/NativeKernels.cs` | 16 | linux | TradingTerminal.Backtest.Engine | product | Y | The built-in native kernels the engine ships, as registry descriptors. Compose a |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/MarketEvent.cs` | 41 | linux | TradingTerminal.Backtest.Engine | product | Y | Which market-data payload a |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/Criteria.cs` | 28 | linux | TradingTerminal.Backtest.Engine | product | Y | Scores a finished run by an |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/GeneticOptimizer.cs` | 123 | linux | TradingTerminal.Backtest.Engine | product | Y | Genetic parameter search for spaces too large to grid exhaustively. A genome |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/GpuUnavailableException.cs` | 9 | linux | TradingTerminal.Backtest.Engine | product | Y | Thrown when the GPU optimizer can't run a job — binary missing, |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/HybridGridOptimizer.cs` | 58 | linux | TradingTerminal.Backtest.Engine | product | Y | Runs a grid sweep on the GPU when it's available and the |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/ProcessGpuOptimizer.cs` | 125 | linux | TradingTerminal.Backtest.Engine | product | Y | C# bridge to the CUDA gpu_optimizer (tools/cpp-backtester/gpu/). Spawns it as a one-shot |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/GridOptimizer.cs` | 64 | linux | TradingTerminal.Backtest.Engine | product | Y | Cartesian product of the axes into one dictionary per combination. |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/TrialRunner.cs` | 31 | linux | TradingTerminal.Backtest.Engine | product | Y | Runs one parameter combination through the engine and scores it — the |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Optimization/WalkForwardOptimizer.cs` | 79 | linux | TradingTerminal.Backtest.Engine | product | Y | Walk-forward analysis: splits the dataset into folds + 1 equal time chunks; |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyDescriptors.cs` | 26 | linux | TradingTerminal.Backtest.Engine | product | Y | Builds |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyKernel.cs` | 127 | linux | TradingTerminal.Backtest.Engine | product | Y | Runs a Python-authored strategy (daxalgo_bt) as a long-lived subprocess and bridges it |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/SimClock.cs` | 15 | linux | TradingTerminal.Backtest.Engine | product | Y | The backtest clock: is whatever the engine last advanced it to as |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Stats/ReportBuilder.cs` | 144 | linux | TradingTerminal.Backtest.Engine | product | Y | Turns a finished run's equity timeline + round-trip ledger into a . |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/Stats/VisualRecorder.cs` | 77 | linux | TradingTerminal.Backtest.Engine | product | Y | Captures the visual-replay backdrop while a run streams: aggregates the charted instrument's |
| `src/linux/Backtest/TradingTerminal.Backtest.Engine/StrategyContext.cs` | 41 | linux | TradingTerminal.Backtest.Engine | product | Y | The engine's |
