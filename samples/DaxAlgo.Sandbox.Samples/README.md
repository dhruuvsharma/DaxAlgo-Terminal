# DaxAlgo sandbox samples

This project is a build-verified open-core example of the two sandbox authoring contracts:

- `MovingAverageCrossKernel` reads scoped one-minute bars, detects fast/slow SMA crosses, and emits
  only `+1` (long) or `0` (flat) reference-unit targets through `IVirtualBook`. Its optional
  protective stop is a model-book intent, not a broker order.
- `SpreadBandVisualizer` auto-runs over scoped bars and quotes, maintains a small public view-state,
  and raises a mediated alert when price moves outside its rolling band. It has no book.

`DaxAlgo.Sandbox.Samples.csproj` references the open-core `DaxAlgo.Sdk` project directly and loads
the shipped forbidden-API analyzer explicitly. `ForbiddenApis.cs.txt` documents examples that would
raise `DAX3001`; its extension intentionally keeps the negative example out of compilation.

The sibling test project demonstrates the public unit-test boundary. It drives the kernel directly
with `ScopedMarketDataView`, `SandboxParameters`, a recording `IVirtualBook`, and
`MediatedAlertSink`; it hosts the visualizer with `SandboxVisualizerRuntime`. Full strategy
backtesting, account access, replication, and live execution belong to the DaxAlgo Terminal/Pro and
are intentionally absent from this public sample.
