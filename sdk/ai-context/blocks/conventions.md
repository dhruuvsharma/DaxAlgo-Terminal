# Writing a unit

A unit is a strategy or a visualizer: **one public class implementing `IUnit`** with a public
parameterless constructor. It is a strategy if it uses the `orders` block, otherwise a visualizer.
Everything it can do comes from blocks; the index lists them and each card shows its calls.

## Shape

```csharp
public sealed class SpreadWatch : IUnit
{
    public UnitInfo Info { get; } = new("Spread watch", "Spread between two instruments.",
    [
        StrategyParameter.Instrument("legA", "Leg A", new InstrumentId(1)),
        StrategyParameter.Instrument("legB", "Leg B", new InstrumentId(2)),
    ]);

    private double _a, _b;

    public Task StartAsync(IUnitContext context, CancellationToken ct)
    {
        var legA = context.Settings.Instrument("legA");
        var legB = context.Settings.Instrument("legB");
        context.Market.OnQuote(legA, q => { _a = q.Mid; Publish(context); });
        context.Market.OnQuote(legB, q => { _b = q.Mid; Publish(context); });
        context.Ui.OnOpened(() => Publish(context));
        return Task.CompletedTask;
    }

    private void Publish(IUnitContext context) =>
        context.Ui.Send("spread", new { a = _a, b = _b, spread = _a - _b });
}
```

## Rules

- **Register in `StartAsync`, then return.** Subscribe, start timers, restore state. Never block or loop there.
- **One thread.** Every handler — data, timers, page messages, posted work — runs one at a time on the
  unit's thread, so fields need no locks. Work from another thread (an awaited HTTP call, a WebSocket
  loop) must come back through `context.Schedule.Post(...)` before it touches a field.
- **Bounded memory.** A unit runs for as long as its window is open: cap every list and dictionary.
- **Time is `context.Clock.UtcNow`**, never `DateTime.UtcNow`.
- **Not allowed:** `System.IO` (files and streams), processes, threads, `System.Threading` timers,
  reflection emit, loading assemblies. Use `state`, `schedule` and `network` instead.
- **Settings change while running.** Handle `context.Settings.OnChanged` and rebuild what depends on
  the changed key.
- **Already imported:** `System`, `System.Collections.Generic`, `System.Globalization`, `System.Linq`,
  `System.Net.Http`, `System.Net.WebSockets`, `System.Text`, `System.Text.Json`, `System.Threading`,
  `System.Threading.Tasks`, `DaxAlgo.Blocks`, `DaxAlgo.Sdk.Quant`, `TradingTerminal.Core.Domain`,
  `TradingTerminal.Core.MarketData`, `TradingTerminal.Core.Strategies.Parameters`. Write `using`
  directives only for anything else.
- **Block ids are not namespaces.** `market`, `math.orderflow`, `ui` name cards. Every type a card lists
  is already imported, so never write `using DaxAlgo.Blocks.Math` or anything like it — call
  `TradeClassifier`, `FootprintTimeBucketer`, `Quote` directly.
- **Read settings by kind:** `Int`, `Number`, `Bool`, `Instrument`, and `Text` for Text, Choice and
  Enum settings. There is no `Choice()` or `Enum()` reader.
- **Small types stay inside your class.** A record or enum only your file needs is declared NESTED in
  your own class, never at the top level: several builders write files at once, and two top-level types
  with one name break the whole unit.

## The page

The window shows `ui/index.html`, which is entirely yours: layout, colours, fonts, charts, controls.
Plain HTML/CSS/JS, or libraries loaded from an https CDN.

```html
<script>
  dax.on("spread", s => { document.getElementById("value").textContent = s.spread.toFixed(2); });
  document.getElementById("reset").onclick = () => dax.send("reset", {});
  dax.ready();
</script>
```

- **`dax` is the terminal's, and it is already there** — a global the window injects before any of the
  page's scripts run, in every file and every module. Never define, wrap, shim or assign your own
  `dax` (`const dax = …`, `window.dax = …`): a page's own copy talks to nothing, so the page never
  becomes ready and the unit never sends it a thing.
- The unit sends whole state per topic (`context.Ui.Send`). Only the latest payload per topic reaches
  the page, so never send deltas that must all arrive.
- The page sends intents back (`dax.send`), and the unit handles them with `context.Ui.On`.
- Call `dax.ready()` after the listeners are attached. The unit's `OnOpened` fires then.
- **A page can be several files**, all under `ui/`: `index.html` plus any `.js`, `.css` or `.svg`
  beside it. Load them with relative paths (`<script type="module" src="app.js">`,
  `<link rel="stylesheet" href="style.css">`, `import { mountScene } from "./scene.js"`). Every file
  the page loads must be one of the unit's files — a missing one fails the gate.
- **3D:** three.js from the CDN through an import map, then plain module imports:

  ```html
  <script type="importmap">
  { "imports": { "three": "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js",
                 "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.170.0/examples/jsm/" } }
  </script>
  <script type="module" src="app.js"></script>
  ```

  ```js
  import * as THREE from "three";
  import { OrbitControls } from "three/addons/controls/OrbitControls.js";
  ```

## Output

Return every file in its own fenced block with its path on the first line:

- C#: a ```csharp block starting `// file: SpreadWatch.cs`
- Page: a ```html block starting `<!-- file: ui/index.html -->`, and optionally ```js
  (`// file: ui/app.js`) and ```css (`/* file: ui/style.css */`) blocks — one block per file, each
  complete and closed

If the brief is ambiguous about the instrument, timeframe, position sizing or risk, ask instead of
guessing.
