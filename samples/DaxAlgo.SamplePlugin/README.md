# DaxAlgo.SamplePlugin

A **minimal, in-tree reference** for the strategy-plugin contract: it references only `DaxAlgo.Sdk`,
exposes one `IStrategyPlugin`, and registers a strategy descriptor + a backtestable
`BacktestStrategyOption`. It is **headless** (no UI) so it shows the bare contract without the WPF view
layer, and it lives in the solution so the SDK surface it uses can't silently break.

> **To start your own plugin, use the template, not this sample:**
> `dotnet new install DaxAlgo.Templates` → `dotnet new daxalgo-strategy -n MyStrategy [--ui]`. The
> template is the canonical, maintained starting point (headless **and** `--ui`, an offline test harness,
> `.daxplugin` packaging, and an AI context pack). See [docs/plugin-authoring.md](../../docs/plugin-authoring.md).

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

Full guide: [docs/plugin-authoring.md](../../docs/plugin-authoring.md) ·
installing/trust: [docs/plugins.md](../../docs/plugins.md).
