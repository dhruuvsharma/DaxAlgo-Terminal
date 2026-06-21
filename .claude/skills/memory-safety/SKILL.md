---
name: memory-safety
description: Memory-leak prevention for DaxAlgo Terminal's WPF windows, charts, tools, and live strategies — the unbounded-channel / per-item-UI-marshal / per-redraw-allocation patterns that ballooned the Volume Footprint window to 20 GB, plus the bounded-channel + batch-drain + coalesced-render fixes and the close-time teardown checklist. Use WHENEVER adding or editing a tool/chart/strategy/AI window, any view-model that consumes IMarketDataHub/Channel/IAsyncEnumerable/IObservable, anything with a DispatcherTimer or a redraw/OnRender path, or when diagnosing climbing RAM / a window that doesn't release memory after closing. Load it before writing streaming UI code, not after the leak.
---

# Memory safety — keep windows/strategies from piling up RAM

This app streams fast feeds into WPF. The recurring failure mode is **decoupling failure**: a hot
producer (tape/quotes/depth) outruns a UI consumer, and the backlog (or never-released subscriptions
/ timers / per-frame allocations) grows without bound. The Volume Footprint window hit **~20 GB over
a few hours and crashed the machine** this way. Every new tool/chart/strategy must be built so the
producer cadence can never pin memory and so the window fully releases on close.

The Stop hook `.claude/hooks/leakcheck-on-stop.ps1` scans changed `.cs` for these patterns and blocks
the turn when it finds them — but the hook is a net, not a substitute for building it right.

## The five leak patterns (and the fix)

### 1. Unbounded channel on a live feed — the canonical 20 GB leak
`Channel.CreateUnbounded<T>()` feeding a UI consumer is **always wrong here**. A fast tape fills it
faster than the UI drains → unbounded backlog + a queued dispatcher closure per item.

```csharp
// WRONG
var ch = Channel.CreateUnbounded<TradePrint>();

// RIGHT — hard memory ceiling, drop the stalest under pressure (accuracy is aggregate, not per-print)
var ch = Channel.CreateBounded<TradePrint>(new BoundedChannelOptions(capacity)
{
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
});
```
Caps used elsewhere: quotes 16k, depth 2k, trades 64k. Pick a ceiling, never `CreateUnbounded`.

### 2. Per-item UI marshal inside the read loop
Marshalling to the UI thread **once per tick/trade** saturates the dispatcher and starves the drain.

```csharp
// WRONG — one Dispatcher hop per trade
await foreach (var t in reader.ReadAllAsync(ct))
    await UiThread.RunAsync(() => OnTrade(t));

// RIGHT — batch-drain: read everything available, marshal ONCE per batch
while (await reader.WaitToReadAsync(ct))
{
    var batch = new List<TradePrint>();
    while (reader.TryRead(out var t)) batch.Add(t);
    await UiThread.RunAsync(() => IngestBatch(batch));   // one hop per batch
}
```

### 3. Redraw per item instead of per frame
Rebuilding a canvas/plot on every event (raising `XxxChanged` from `OnTrade`/`OnTick`) makes render
cadence = feed cadence. Decouple: events set a `_dirty` flag; **one** `DispatcherTimer` (~80 ms /
~12 fps) redraws if dirty. Move `XxxChanged`/plot refresh **out** of the per-event handler into the
render tick. This also kills accidental O(n²) rebuild-per-item.

### 4. Per-redraw allocations
Allocating `Typeface`/`FontFamily`/`SolidColorBrush`/`Pen`/`FormattedText` per element per redraw
churns the GC and inflates working set. Hoist them to **cached `static readonly`** fields (e.g. the
`FontFamily("Consolas")` fix) and `Freeze()` brushes/pens.

### 5. Subscriptions / timers / handlers not released on close
A window that subscribes to a hub/observable, starts a timer, or adds an event handler and **doesn't
undo it on close** pins the whole VM (and its buffers) for the app's life — RAM never drops after you
close the window. Make the VM `IDisposable` and tear everything down:

- Rx: store `IDisposable`s in a `CompositeDisposable` (or `_subscriptions`) and dispose in `Dispose()`.
- `DispatcherTimer`: `.Stop()` in `Dispose()`.
- `+=` event handlers: a matching `-=` in `Dispose()`.
- Cancel the pump: cancel the `CancellationTokenSource` driving the read loop.

The shell already disposes a tool/strategy VM on window `Closed` **iff it implements `IDisposable`**
(`OpenHostedTool`/`OpenWindowTool`/`OpenStrategy` do `if (vm is IDisposable d) d.Dispose()`), so the
contract is: **own a resource ⇒ implement `IDisposable` and release it there.**

## Checklist for any new tool / chart / strategy / AI window

- [ ] Every `Channel` feeding the UI is **bounded** with `DropOldest` (never `CreateUnbounded`).
- [ ] The read loop **batch-drains** and marshals to the UI **once per batch**, not per item.
- [ ] Redraw is **coalesced** by a render timer (dirty flag), not raised per event.
- [ ] Brushes/pens/typefaces/fonts are **cached static** and frozen; nothing heavy `new`'d in a draw loop.
- [ ] The VM is **`IDisposable`** and `Dispose()` cancels the CTS, stops timers, disposes Rx subs, and `-=`'s handlers.
- [ ] Bounded buffers for any retained history (ring buffer / cap + trim), not an ever-growing `List`/`ObservableCollection`.
- [ ] `ScottPlot`/Helix/WebView2 surfaces: clear/replace series each render rather than appending forever.

## Reference: already-correct VMs to copy from
`LiveSignalStrategyViewModelBase` (UI — bounded quote/depth/trade pumps + batch-drain), `VolumeFootprintViewModel`,
`CumulativeDeltaViewModel`, `OrderFlowCubeViewModel`, `OrderBookViewModel`, `SingleInstrumentHeatmapViewModelBase`,
`OrderFlowPressureMap` (locked row state + snapshot timer), `IndexKScoreSurfaceViewModel` (coalesced render).

## Verify

```powershell
# Static scan of the working tree for the patterns (same engine the Stop hook runs):
powershell -NoProfile -ExecutionPolicy Bypass -File .claude\hooks\leakcheck-on-stop.ps1 -All
```
For a real leak suspicion, run the app, open/close the window many times, watch the process working
set settle back down (Task Manager / `dotnet-counters`). RAM that doesn't drop after Close ⇒ pattern 5.

## What NOT to do
- Don't write `Channel.CreateUnbounded` for a live-feed consumer — there is no valid case here.
- Don't raise a redraw / `XxxChanged` from inside `OnTick`/`OnTrade`/`OnDepth` — set dirty, render on the timer.
- Don't subscribe / start a timer / `+=` a handler in a VM that isn't `IDisposable`.
- Don't trust a doc-comment that says "a bounded channel decouples…" — check the actual `Create*` call (several lied).
