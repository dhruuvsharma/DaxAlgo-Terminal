# index/Backtest — per-file index (Windows tree)

Generated 2026-07-17. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Accounting/Portfolio.cs` | 152 | win | TradingTerminal.Backtest.Engine | B I P | Y | Update the latest mark for an instrument and the open lots' favorable/adverse |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/BacktestEngine.cs` | 130 | win | TradingTerminal.Backtest.Engine | B I P | Y | Drives one backtest end-to-end. Single-threaded by design: there is one logical timeline |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Cost/FeeModels.cs` | 16 | win | TradingTerminal.Backtest.Engine | B I P | Y | Maps the serializable |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/EngineOrderRouter.cs` | 47 | win | TradingTerminal.Backtest.Engine | B I P | Y | The kernel-facing order seam for the backtester. Resolves each 's |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/IFillModel.cs` | 73 | win | TradingTerminal.Backtest.Engine | B I P | Y | Decides whether a working order fills against the current quote and at |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/SimulatedOrderBook.cs` | 94 | win | TradingTerminal.Backtest.Engine | B I P | Y | Raised for every order transition: the instrument the order trades, then the |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Execution/WorkingOrder.cs` | 19 | win | TradingTerminal.Backtest.Engine | B I P | Y | A live order resting in the simulated book, tagged with the instrument |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/AsyncMerge.cs` | 49 | win | TradingTerminal.Backtest.Engine | B I P | Y | K-way merge of already-ascending streams into one globally |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/IMarketDataFeed.cs` | 14 | win | TradingTerminal.Backtest.Engine | B I P | Y | Produces the time-ordered event stream the engine replays for a run. Implementations |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/InMemoryMarketDataFeed.cs` | 28 | win | TradingTerminal.Backtest.Engine | B I P | Y | A feed backed by an in-memory event list — the workhorse for |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/StoreMarketDataFeed.cs` | 50 | win | TradingTerminal.Backtest.Engine | B I P | Y | Replays a run from the canonical market-data store — the primary data |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticMarketDataFeed.cs` | 48 | win | TradingTerminal.Backtest.Engine | B I P | Y | A deterministic synthetic feed: a mean-reverting (Ornstein-Uhlenbeck-ish) random walk of the mid |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Feeds/SyntheticTapeFeed.cs` | 95 | win | TradingTerminal.Backtest.Engine | B I P | Y | Default anchor: a weekday in the London/NY overlap so session gates pass. |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/BacktestStrategyKernelAdapter.cs` | 50 | win | TradingTerminal.Backtest.Engine | B I P | Y | Wrap an already-built legacy strategy (used by the parity test). |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/MeanReversionKernel.cs` | 81 | win | TradingTerminal.Backtest.Engine | B I P | Y | Catalog descriptor — its tunable surface for the Studio, the optimizer, and |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Kernels/NativeKernels.cs` | 16 | win | TradingTerminal.Backtest.Engine | B I P | Y | The built-in native kernels the engine ships, as registry descriptors. Compose a |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/MarketEvent.cs` | 41 | win | TradingTerminal.Backtest.Engine | B I P | Y | Which market-data payload a |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Criteria.cs` | 28 | win | TradingTerminal.Backtest.Engine | B I P | Y | Scores a finished run by an |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/GeneticOptimizer.cs` | 123 | win | TradingTerminal.Backtest.Engine | B I P | Y | Genetic parameter search for spaces too large to grid exhaustively. A genome |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/GpuUnavailableException.cs` | 9 | win | TradingTerminal.Backtest.Engine | B I P | Y | Thrown when the GPU optimizer can't run a job — binary missing, |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/HybridGridOptimizer.cs` | 58 | win | TradingTerminal.Backtest.Engine | B I P | Y | Runs a grid sweep on the GPU when it's available and the |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/Gpu/ProcessGpuOptimizer.cs` | 125 | win | TradingTerminal.Backtest.Engine | B I P | Y | C# bridge to the CUDA gpu_optimizer (tools/cpp-backtester/gpu/). Spawns it as a one-shot |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/GridOptimizer.cs` | 64 | win | TradingTerminal.Backtest.Engine | B I P | Y | Cartesian product of the axes into one dictionary per combination. |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/TrialRunner.cs` | 31 | win | TradingTerminal.Backtest.Engine | B I P | Y | Runs one parameter combination through the engine and scores it — the |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Optimization/WalkForwardOptimizer.cs` | 79 | win | TradingTerminal.Backtest.Engine | B I P | Y | Walk-forward analysis: splits the dataset into folds + 1 equal time chunks; |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyDescriptors.cs` | 26 | win | TradingTerminal.Backtest.Engine | B I P | Y | Builds |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Polyglot/PythonStrategyKernel.cs` | 127 | win | TradingTerminal.Backtest.Engine | B I P | Y | Runs a Python-authored strategy (daxalgo_bt) as a long-lived subprocess and bridges it |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/SimClock.cs` | 15 | win | TradingTerminal.Backtest.Engine | B I P | Y | The backtest clock: is whatever the engine last advanced it to as |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Stats/ReportBuilder.cs` | 144 | win | TradingTerminal.Backtest.Engine | B I P | Y | Turns a finished run's equity timeline + round-trip ledger into a . |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/Stats/VisualRecorder.cs` | 77 | win | TradingTerminal.Backtest.Engine | B I P | Y | Captures the visual-replay backdrop while a run streams: aggregates the charted instrument's |
| `src/windows/Backtest/TradingTerminal.Backtest.Engine/StrategyContext.cs` | 41 | win | TradingTerminal.Backtest.Engine | B I P | Y | The engine's |
