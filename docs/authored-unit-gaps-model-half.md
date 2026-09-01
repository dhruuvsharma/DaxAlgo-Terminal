# The model half of the benchmark

`authored-unit-gaps.md` measures the **contract**: what a hand-written authored unit cannot express,
using `samples/DaxAlgo.Sandbox.Samples/LiquidityBookVisualizer.cs` as a control written with full
knowledge of the SDK and no model involved. Everything there is missing from the SDK, and a model
cannot be marked down for any of it.

This file is the other half: **what a model actually produced, on the same brief, against the same
window.** Kept separate on purpose — a shortfall that belongs to the contract and a shortfall that
belongs to the model need different work, and merging them makes both unattributable.

## How to reproduce it

`HyperionBenchmark` (in `tests/TradingTerminal.Plugins.Tests`) is the drive, committed so a number can
be compared to the next one rather than remembered.

```powershell
# offline — runs on every build, and exercises the whole driver against a canned reply
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~HyperionBenchmarkTests

# live — spends tokens
$env:OPENROUTER_API_KEY = "…"; $env:HYPERION_LIVE = "1"
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~A_model_answers

# re-judge a unit a previous run produced, without paying for another generation
$env:HYPERION_REVERIFY = "…\artifacts\hyperion-benchmark\<run>\BookHeatmapVisualizer.cs"
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~Reverify_a_saved_unit
```

Artifacts land in `artifacts/hyperion-benchmark/<utc>-<label>/`: the composed system prompt, the full
transcript, the generated source, and a summary table. The provider comes from the environment —
`HYPERION_LIVE_KEY`, `HYPERION_LIVE_BASE_URL`, `HYPERION_LIVE_MODEL`, `HYPERION_LIVE_PROVIDER` —
because the useful free model changes month to month and a benchmark hard-wired to one vendor stops
being run the day that vendor stops being the answer. The defaults are the openrouter free tier.

**The artifacts land under the build output**, not the repo: the tests run from a redirected `bin`, so
the repo-root walk that looks for `TradingTerminal.Windows.slnx` does not find one. Look under
`…\bin\Debug\net9.0-windows7.0\artifacts\hyperion-benchmark\`.

**The offline and live runs differ by one constructor argument.** That is the point: the driver — the
composition, the escape reply, the compile, the ladder, the artifact writing — is exercised on every
build, so it cannot rot between the live runs that are months apart.

## Run 1 — 2026-09-01

| | |
|---|---|
| provider | openrouter, `minimax/minimax-m3:free` |
| why that model | the account is **free tier**, so a paid model returns HTTP 402. This is the weakest realistic case, deliberately |
| kind / effort | Visualizer / Standard (single conversation, 3 skill packs, 2 fix attempts) |
| brief | *"An order book window: the depth ladder, a liquidity heatmap over time, and the microstructure statistics."* |
| system prompt | 109,247 chars, of which the surface cut saved 17,332 |
| skills selected from the brief | order flow · drawing · quant math |
| turns | **1 — it did not interview, it built** |
| generations | 2 (one auto-fix) |
| wall clock | 210.4 s |
| tokens | 57,879 in / 47,223 out |
| output | `BookHeatmapVisualizer.cs`, **441 lines / 19,472 chars** |
| ladder | Lifecycle ✓ · SchemaCoherence ✓ · DrawProbe ✓ · Replay n/a |

For scale: the control is 439 lines and the hand-written `TradingTerminal.OrderBook` is a 1,154-line
view-model plus 492 lines of XAML plus 448 of code-behind.

**It did not ask anything.** The previous run (2026-08-31, recorded in the loop brief) asked three
well-formed questions on a vaguer brief. This brief names its three elements, so there was less to
settle — which is the interview working, not failing, but it means the questioning path was not
exercised here and the "Just build it" escape was never sent.

## What it got right

Nothing in this list was prompted for beyond the one line above.

- **The arrangement**, matching the control almost exactly: heatmap dominant (`Star(5)`), ladder as a
  fixed 240px column beside it, statistics as an 80px strip beneath. Declared as a `UnitLayout` with
  one callback per panel.
- **Seven parameters**, grouped (Market / Book / Heatmap / Flow), each with min, max, step and unit.
- **Both verbs, and the take-away.** `Reset heatmap` and `Copy book`, the latter through
  `context.Export.Offer(…)` — inside `OnActionAsync`, which is the only place an offer is honoured.
- **All three data callbacks**: depth drives the heatmap and the ladder, quotes sign the prints and
  feed the spread statistic, prints feed order-flow imbalance and VPIN.
- **The maths from the library, not by hand**: `Book.Microprice`, `Book.Imbalance`,
  `Book.SweepSlippage` (both sides), `TradeClassifier`, `OrderFlowImbalance`, `Vpin`, `SpreadStats`.
- **Bounded memory**: a ring of heatmap columns, each rebased to the current mid at draw time rather
  than at capture, so a price move does not push history out of view.
- Two things the control does *not* do: it exposes the VPIN bucket size as a parameter rather than
  hard-coding 500, and it infers the instrument's tick size from the first two-level snapshot, with a
  comment saying why (the host does not surface one).

## What it missed — the model's half

### 1. It used no gestures at all

Zero references to `Cursor`, `Viewport` or `Crosshair`. The control uses four: a pinned price row from
`Cursor.HasSelection`, `Viewport.Zoom` and `PanX` to choose the visible window, and
`LadderOptions.FirstLevel` driven from `PanY` to scroll the book.

And it did the thing the gesture work exists to remove: **`heatmapColumns` is a parameter**. That is
precisely the workaround the control deleted on 2026-08-31 once zoom existed — "how much history do I
want" answered by typing a number into a form rather than by turning a wheel over the picture.

**Diagnosis: teaching, not contract.** The capability shipped, it is documented in the surface, and the
exemplar `BookPressureVisualizer` demonstrates it. It still did not transfer. The most likely reason is
weight: gestures are three lines in a 300-line exemplar and one entry among many in the surface, while
the *shape* of a unit — schema, callbacks, layout, draw — is the whole exemplar and was copied
faithfully. **A model imitates the exemplar far more strongly than it reads the reference**, which the
loop brief already states as measured, and this is another instance of it.

### 2. No crosshair and no hover readout

Related to the above and worth listing separately, because it needs no gesture *state* — `RenderCursor`
is a read and `RenderSurfaceView` invalidates on `MouseMove`, so a crosshair costs one call. The control
draws price and size at the pointer over the heatmap.

### 3. No signed-imbalance lane

The control draws a `Histogram` of signed imbalance under the heatmap; the model drew no fourth picture.
Minor — the same number is in the strip as a tile — but it is one less thing to read at a glance.

### 4. Its `Draw` fallback does not open a panel

Both exemplars open `using var panel = surface.Panel(…)` in `Draw` and do **not** in their layout
callbacks, because the layout host owns the panel region and header. The generated unit copied the
second half and not the first, so its single-surface fallback draws unclipped and untitled.

Invisible in the real window, which uses the layout. Visible in the authoring **preview**, which does
not.

## What this run found in the HARNESS, not the model

The first verdict on this unit was **DrawProbe failed: `draw.no-panel`**. That was wrong, and chasing
it found a defect worth more than the run.

`AuthoredUnitVerifier` drove `unit.Draw` and nothing else. But a unit that declares a `UnitLayout` is
rendered by `AuthoredUnitLayoutHost`, which builds one surface per panel and binds it to that panel's
own callback — `Draw` is not called at all, and the SDK documentation says so in as many words: *"Draw
is then unused, because the panels do the drawing."*

So rung 7 was judging the one method the host does not use, and never touching the three it does. Wrong
in both directions:

| | |
|---|---|
| A correct unit that declares a layout and leaves `Draw` at its default | **failed** with `draw.blank` |
| A unit whose visible panel throws, emits NaN, or blows the frame budget | **passed**, as long as the unused fallback was fine |

The second direction ships a broken window. The first costs money: `AuthoringJudge` turns a rung
failure into a repair turn, so on the Deep and Max paths a false failure spends a whole generation
asking a model to rewrite working code — the exact thing that class's own doc warns about.

Fixed by `DrawProbe.RunLayout`, which walks the layout and judges every panel, naming findings by panel
("Panel 'Book': …") because *"something in this window draws nothing"* is not actionable. `draw.no-panel`
is asked only of `Draw`, never of a panel callback, since the host already owns that region.

**Re-judged against the corrected ladder, the generated unit clears every applicable rung.** The
shortfalls in the list above are real; the one the ladder reported was not.

This is the ninth defect in this area of the shape *built, unit-tested, never reached on the path that
runs* — and the first where the thing not reached was the **verifier's own subject**. `DrawProbe` has
thirteen tests and every one of them hands it a callback directly.

### Still unreached, found while fixing the above

`DrawProbe.RunDegenerate` — the second pass against a zero-sized viewport, which the file itself
motivates with "a panel collapsed to nothing, a window restored minimised, a layout pass before
measurement" — **is called by no verifier**. Only by tests. So a unit that divides by the viewport
still reaches the render thread of a running application. Left open deliberately: adding it makes the
ladder stricter, which changes which generated units pass, and that deserves its own measurement rather
than being smuggled into this one.

## Run 2 — 2026-09-01, a stronger model, and a worse window

| | run 1 | run 2 |
|---|---|---|
| provider | openrouter `minimax/minimax-m3:free` | tokenrouter `z-ai/glm-5.3-free` |
| turns / generations | 1 / **2** (one auto-fix) | 1 / **1** — compiled first try |
| wall clock | 210 s | **1,017 s** |
| tokens in / out | 57,879 / 47,223 | 29,559 / **69,370** |
| output | 441 lines | **677 lines** |
| ladder | **clears every rung** | **fails rung 7** |

The stronger model wrote half again as much code, compiled it on the first attempt without a single
fix, and produced more output tokens than it consumed input — it reasons hard and it shows. It also
produced a window whose **main panel never paints**, and no amount of reading the source would tell
you that.

### The defect, and it is a good one

`Panel 'Liquidity': After data arrived the unit still drew only text.`

The heatmap infers the instrument's tick width from the book, because the host does not expose one —
the same instinct run 1 had, and a good one. But:

```csharp
if (_tickWidth > 0d)                       // ← the gate
{
    if (_running is null || …) { FinalizeColumn(); UpdateTick(); … }   // ← the only caller
    Accumulate(depth);
}
```

`UpdateTick()` is the only thing that ever assigns `_tickWidth`, and it is reachable only from inside
a branch guarded by `_tickWidth > 0d`. **A bootstrap deadlock.** The tick width is never learned, so no
time slice ever closes, so no column is ever accumulated, so the dominant panel of a 677-line window
draws *"Learning the price grid…"* for as long as it is open.

It compiles. It runs. It clears lifecycle and schema coherence. Every statistic in its strip is
correct. Reviewing the source, the gate reads as ordinary defensive programming.

**Attribution checked, not assumed.** The obvious suspicion was the harness — the drive's clock was
frozen, so a unit slicing by wall clock would close no bucket for a reason that is ours. That was a
real gap and it is fixed (below); the unit was then re-judged against the advancing clock and returns
**the same verdict**, because the deadlock does not involve time at all.

**This is the case rung 7 exists for**, and it is only visible because the ladder now judges the
panels the host renders rather than the unused `Draw` fallback. Judged the old way this unit passes.

### Two things the run found in the harness

**1. `SyntheticDrive` supplied data but not time — fixed.** Its clock was frozen at the epoch while its
bars marched a minute apart, so a hundred and twenty bars of market data arrived in zero seconds. A
liquidity heatmap slicing every second — the ordinary way to build one — closes no slice in the whole
drive and draws its warm-up message forever, and rung 7 then reports a blank panel for a unit that
would paint perfectly against a real feed. That is the expensive direction: it sends a repair agent to
rewrite working code. The clock now advances with the revealed bars, and stays deterministic because it
is the bar series rather than the machine's clock. Exactly the same omission as feeding no depth and no
tape, one file over and one iteration later.

**2. `AiCodegenOptions.TimeoutSeconds` does not bound a streaming generation.** Observed, not reasoned:
`HttpClient.Timeout` was 15 minutes, the generation took 17, and nothing fired. The streaming path
sends with `HttpCompletionOption.ResponseHeadersRead`, so the timeout covers the header phase only and
the body is read under the caller's token alone. The factory's own doc calls it *"one generation's wall
clock"*. In the app the Stop button supplies a token so a user can escape; a scripted caller passing
`default` cannot, and a provider that opens a stream and goes quiet hangs forever. **Not fixed** — an
idle timeout is the right shape (a reasoning model legitimately emits nothing for minutes, so a total
wall clock is wrong), and it deserves its own change rather than being folded into this one.

Also worth recording: that provider rate-limits at 8 requests/minute, which the fix loop can exceed on
its own; the first attempt died on a 429 at the first request. The benchmark reported it as a
`ProviderError` and failed loudly, which is right — a provider failure is ours, not the model's.

### What run 2 changes about the conclusions

**First-generation compile success is not the metric.** The model that needed a repair turn produced
the working window; the one that needed none produced a broken one. Anything that scores authoring on
compiles-per-generation would rank these two backwards.

**The run-1 gesture finding has to be narrowed, and that is what a second data point is for.** Run 2
used **all four**: `Viewport.Zoom` and `PanX` choose its visible history, `PanY` feeds
`LadderOptions.FirstLevel` so the book scrolls, and `Cursor.HasSelection` pins a price row. So the
capability does transfer, the documentation and the exemplar are sufficient for a model that is
strong enough, and *"gestures are a teaching gap"* is wrong as stated. What is true is narrower:
**gestures are the first thing to go at the weak end.** They are the part a struggling model drops
while still producing something that compiles and looks complete — which is worth knowing, because it
is invisible in every check except a human opening the window.

## The delta table

Kept so the next run has something to move.

| | run 1 · minimax-m3 | run 2 · glm-5.3 |
|---|---|---|
| compiles | ✓ (2 generations) | ✓ (1 generation) |
| clears the ladder | ✓ (after the verifier fix; ✗ before it, wrongly) | **✗ — its main panel never paints** |
| the three panels of the brief | ✓ | ✓ (two of them work) |
| microstructure maths from the library | ✓ | ✓ |
| verbs + take-away | ✓ | ✓ |
| gestures (pin / zoom / pan / scroll) | ✗ — none | ✓ — all four |
| crosshair + hover readout | ✗ | ✗ |
| a picture per statistic | partial — tiles, no imbalance lane | partial |
| `Draw` fallback well-formed | ✗ — no panel scope | ✓ |
| asked the user anything | ✗ — built directly | ✗ — built directly |

Neither run drew a crosshair or a hover readout, and neither asked a question on this brief. Those are
the two things to watch on run 3.
