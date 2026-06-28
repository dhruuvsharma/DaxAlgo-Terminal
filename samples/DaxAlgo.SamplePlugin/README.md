# DaxAlgo.SamplePlugin

A minimal, copyable **strategy plugin** for DaxAlgo Terminal — the starting point for building your
own. It references only `DaxAlgo.Sdk`, exposes one `IStrategyPlugin`, and registers a strategy
descriptor + a backtestable `BacktestStrategyOption`. It is **headless** (no UI) so it shows the
contract without the WPF view layer.

## Build

```bash
dotnet build samples/DaxAlgo.SamplePlugin
```

Output: `bin/<config>/<tfm>/DaxAlgo.SamplePlugin.dll`.

## Test it in the app

1. Run the terminal, open **Plugins → Manage strategy plugins…**, click **Install plugin…**, and pick
   `DaxAlgo.SamplePlugin.dll`. (Or copy the build folder to `<app>/plugins/DaxAlgo.SamplePlugin/` by hand.)
2. **Restart** the app. "Sample Plugin Strategy" appears in the strategy catalog and in Backtest Studio.

## Test it headlessly (CLI)

```bash
# stage next to the daxalgo-backtest exe
mkdir -p <cli>/plugins/DaxAlgo.SamplePlugin && cp DaxAlgo.SamplePlugin.dll <cli>/plugins/DaxAlgo.SamplePlugin/
daxalgo-backtest synth --output demo.parquet --ticks 2000
daxalgo-backtest run --strategy sample.plugin --symbol TEST --data demo.parquet
```

## Make it your own

- Replace `SampleBacktestStrategy` with your real engine logic (place orders via the `IOrderRouter`).
- Rename the id (`sample.plugin`), display name, and description.
- Add a `plugin.json` manifest (see the full guide).
- For a live strategy *window*, add a reference to `DaxAlgo.Sdk.Wpf` and register a view + view-model.

Full guide: [docs/plugins.md](../../docs/plugins.md).
