# Legacy AI context pack

`daxalgo-strategy-context.md` is the generated prompt for the in-app Hyperion authoring path. It
describes the `IBacktestStrategy` kernel workflow and is **not** the SDK 0.3 sandbox contract.

It is embedded into `DaxAlgo.Codegen` and shipped in the application, so a wrong statement here reaches
a model and lands in generated strategies. Keep it accurate. The `.daxalgostrategy` packaging it was
originally written alongside is retired; `.daxalgostrategy` and `.daxalgovisualizer` are the accepted
package formats.

For new agent or human authoring, use the public
[`docs/sandbox-authoring.md`](../../docs/sandbox-authoring.md) contract and the
`daxalgo-sandbox-strategy` / `daxalgo-sandbox-visualizer` templates. Those target `DaxAlgo.Sdk` 0.3 and
use `IStrategyKernel` / `IVisualizer` capability contexts.

The legacy pack is generated rather than hand-maintained. Its generator still references the retired
`templates/content/daxalgo-strategy/` scaffold, so do not run it until the Hyperion compatibility path
is migrated or formally retired:

```powershell
# Currently blocked by the removed legacy template; retained for migration work only.
pwsh build/gen-ai-context.ps1
```

Do not hand-edit the generated pack to make it look current. Migrating it changes runtime authoring
behavior and must update the generator, embedded consumer, tests, and generated output together.

## Current legacy consumers

- **Hyperion / in-app AI builder** — the embedded legacy output contract.
- **`daxalgo strategy ai`** — the older source-run authoring CLI.

Everything the pack drives is local except the prompt + pack sent to the user's chosen provider.
