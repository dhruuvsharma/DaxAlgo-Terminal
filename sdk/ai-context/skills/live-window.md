---
id: live-window
name: The live window (view-model, view, memory safety)
triggers: window, view, ui, panel, chart, display, show, render, dashboard, visual, plot, gui, screen, live view, viewmodel, wpf
---

# The live window

The host owns the hard parts. The view-model you write supplies two things: what data the strategy needs,
and how to build its kernel.

## What the base already gives you (do not rebuild these)

`LiveSignalStrategyViewModelBase` owns the instrument picker, warm-up, start/stop, the market-data pumps,
the signal feed, presets, and the Activity Log. It exposes:

| Member | What it is |
|---|---|
| `Signals` | `ObservableCollection<SignalEntry>` — every signal the strategy fired, bounded |
| `Bars` | `ObservableCollection<Bar>` — the warm-up + live bars |
| `BarsChanged`, `TickProcessed` | events, for a chart that needs to redraw |
| `Log(category, message)` | writes to the ONE app-wide Activity Log |
| `StrategyId`, `StrategyDisplayName` | identity |

You override `DataRequirement` and `BuildStrategy(contract)` — and `BuildStrategy` must return **the same
kernel the backtest runs**, which is what stops live and backtest from diverging.

## Exposing live internals to the view

Mirror engine state onto `[ObservableProperty]` fields on the view-model, updated from the kernel — do not
have the view reach into the kernel. Keep any history the view draws in a **bounded** buffer owned by the
view-model.

## Memory safety (this is where live windows go wrong)

A feed is thousands of events a second. The rules, in order of how much damage they prevent:

1. **Never redraw per event.** Coalesce: set a dirty flag, redraw on a `DispatcherTimer` (say 20–30 Hz).
   A redraw per trade is what turned an earlier window into 20 GB of RAM.
2. **Never marshal to the UI per event.** Batch-drain a bounded channel; one UI post per batch.
3. **Bound every buffer.** A `List` you only ever append to is a leak with extra steps.
4. **Dispose what you own.** Timers, subscriptions, event handlers — unhook them on close.
5. **Allocate nothing in the hot path.** `OnTickAsync` runs per tick, in a backtest too.

## The view

Roslyn cannot compile XAML, so an authored view builds its tree in C#. Keep it modest — a signals list and
the two or three numbers that explain the strategy's state beat a dashboard that doesn't compile:

```csharp
using System.Windows.Controls;
using System.Windows.Data;

public sealed class MyStrategyView : UserControl
{
    public MyStrategyView()
    {
        var signals = new ListBox();
        signals.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Signals"));
        Content = signals;
    }
}
```

If you write no view at all, the host composes a default one from the strategy's `DataRequirement` — so
prefer writing nothing over writing a view you are unsure of.
