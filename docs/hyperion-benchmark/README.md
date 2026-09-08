# The Hyperion benchmark

Twenty briefs, each with a picture somebody already sells, and a rubric that says which half of this
codebase is at fault when the answer falls short.

## Why this becomes the primary benchmark

The six briefs in `.claude/context/tasks/hyperion-window-briefs.md` were written from **our own**
hand-written windows. They stay — each has a control, and `docs/authored-unit-gaps.md` is built on
them — but they can only measure whether Hyperion has caught up with us. "As good as
VolumeFootprint" is not the bar a user has in their head when they type a brief. The bar is
Bookmap, NinjaTrader, Sierra Chart and TradingView, because that is what is open on their other
monitor.

So this suite is written from **what the industry sells**: for each brief, the named product, the
feature it is marketed as, and a link to the vendor's own page for it. The target is not a
description we invented; it is a screenshot anybody can pull up.

| | Old six | This twenty |
|---|---|---|
| Written from | our hand-written windows | competitors' shipped features |
| Has an in-repo control | yes (`LiquidityBookVisualizer`) | no |
| Answers | "has Hyperion caught up with us" | "would a user recognise what came out" |

## Two tiers, and why both are run

Each brief exists twice, and they measure different things.

**ASK** is one line — what a user actually types. `create a orderbook chart visualizer` is a real
example, and the run that produced it is what prompted this document. This tier measures Hyperion's
**questions and its defaults**: a one-liner cannot specify a window, so everything not asked about is
a default, and a bad default is invisible until somebody looks at the result.

**SPEC** is a paragraph — the same window, described fully, in a user's voice, still naming no type
and no widget. This tier measures **execution against a specification**, with the questioning removed
as a variable.

The gap between the two is the most actionable number here. Ask ≪ Spec means the questioning is the
problem. Ask ≈ Spec, both low, means the drawing or the maths is.

## The rubric

Seven axes, **0–3** each, 21 total. Three is "a user of the reference product would recognise this";
two is "close, with something wrong"; one is "attempted and wrong"; zero is "absent".

| | Axis | The question | Usual owner when it fails |
|---|---|---|---|
| **R** | Runs | Compiled, cleared the verification ladder, drew a non-blank first frame | harness |
| **D** | Data | Declared the requirement the picture needs, and the streams actually arrive | product (feed) / harness (declaration) |
| **M** | Maths | The quantities mean what the reference means — delta is delta, the value area is 70 %, VPIN buckets by volume and not by time | harness (teaching) / product (missing estimator) |
| **L** | Layout | Panes, proportions and furniture — gutter, time axis, legend, readout strip — placed as the reference places them | harness (drawing pack) / product (missing widget) |
| **V** | Visual | Colour carries the same meaning, density is readable, labels say what the reference's labels say | harness |
| **I** | Interaction | Crosshair and hover readout, zoom, pan, selection, declared verbs | product (contract) / harness (unused) |
| **S** | Survives | Empty first frame, tiny panel, no NaN, bounded buffers, no work in `Draw` | harness (rules) |

**R is a gate.** R = 0 scores the run zero and the rest is not judged — record why it did not run and
stop. A picture that does not exist cannot be a two on layout.

Score with the reference open beside the screenshot. This is a judged benchmark and is meant to be:
nothing automatable answers "would a user recognise this", which is the only question that matters.

## Every deduction names an owner

A score with no attribution is a complaint. Each point lost is one line tagged `harness:` or
`product:`, rolling into the two documents that already exist:

- **`product:`** — the contract cannot express it → [`authored-unit-gaps.md`](../authored-unit-gaps.md).
  A model cannot be marked down for these, and the note says so.
- **`harness:`** — the contract can express it and the model did not →
  [`authored-unit-gaps-model-half.md`](../authored-unit-gaps-model-half.md). Prompt pack, skills,
  exemplars, verification ladder, agent loop.

That separation is not new and not optional. It is why the existing loop produced fixable work
instead of a mood.

## Cost is recorded beside quality

Every run records prompt characters, input tokens, wall-clock seconds, generations, model and
effort. A change that doubles fidelity and triples latency is a trade somebody has to make
deliberately, and it cannot be made from a quality column alone.

## The pinned configuration

Measured 2026-09-01/02 and unchanged since; re-measure rather than assume.

- **Baseline**: `minimax/minimax-m3:free` via openrouter, **Standard** effort. Every compiling unit
  the loop has produced on a free model was at Standard. Deep and Max route through the six-agent
  path and, on a free model, reason to the output cap without ever starting an answer.
- **Ceiling**, when credits exist: the strongest available model, Standard and Deep, reported as its
  own column. The free-model number is the weakest realistic case, not the expected one.
- Space the briefs. One long brief has exhausted the free-tier quota and taken the next five with it.
- Verify a model id against `GET {BaseUrl}/models` first: a wrong id fails exactly like a bad key.
- Record the commit of `sdk/ai-context/` at run time. A score against an unrecorded prompt is not a
  measurement.

## Running one

```powershell
$run = "docs/hyperion-benchmark/runs/2026-09-08-baseline"
mkdir $run -Force
# 1. Paste the ASK line (or the SPEC paragraph) into Hyperion at the pinned config.
# 2. Save what it produced as $run/S3-ask.cs

# 3. Photograph it: compiles the unit, drives it with synthetic depth, a tape and 120 bars through
#    the real lifecycle, renders it through the real layout host, writes a PNG.
$env:HYPERION_SHOT_SOURCE = "$run/S3-ask.cs"
dotnet test tests/TradingTerminal.App.Basic.Tests/TradingTerminal.App.Basic.Tests.csproj `
    --filter "FullyQualifiedName~AuthoredUnitScreenshot"

# 4. Score against the reference and write the row.
```

The screenshot path matters: it renders through `AuthoredUnitLayoutHost`, the control the real window
uses, driven by the same `SyntheticDrive` the verifier uses. A picture from a mock would be a picture
of the mock.

## The scoreboard

One table per run in `runs/<date>-<label>.md`. Copy this shape.

```
| # | Brief | Tier | R | D | M | L | V | I | S | /21 | s | gens | Note |
|---|-------|------|---|---|---|---|---|---|---|-----|---|------|------|
| S1 | Indicator confluence | ask  |   |   |   |   |   |   |   |     |   |      |      |
| S1 | Indicator confluence | spec |   |   |   |   |   |   |   |     |   |      |      |
```

Then the deductions, tagged:

```
S1 ask  L-1 harness: no oscillator pane; RSI drawn on the price scale, so it is a flat line near 50.
S1 ask  I-2 product: no way to give a pane its own scale. -> authored-unit-gaps.md
```

## The loop this feeds

1. Run the suite at the pinned config. That is the **baseline**, and the only thing a later run is
   compared against.
2. Read the deductions, group them, pick **one cause** — not one symptom.
3. Fix it properly, in the half that owns it.
4. Re-run the briefs that fix should have moved, and report the delta per axis.
5. Re-run the whole suite before believing it. A fix that lifts three briefs and drops two has not
   worked, and only the full re-run says so.

One real cause fixed properly beats three features nobody has run.

## Known limits these briefs will hit

Recorded once here so a run does not rediscover them, and so a `product:` deduction against one is
written once rather than twenty times.

- **A strategy may name exactly one instrument.** `SandboxStrategyRuntime.ResolveInstruments`
  requires exactly one resolved instrument parameter and throws otherwise, so every pairs, spread or
  cross-asset strategy is out of reach *as a strategy*. That is why there is no stat-arb brief here;
  it is the first to add when the limit lifts. Visualizers are unaffected.
- **No options data.** No chain, no greeks, no dealer positioning anywhere in the pipeline. `V9` is
  in the suite deliberately and is scored on **honesty** rather than fidelity: a unit that says it has
  no options data is right, and one that draws a plausible gamma profile out of nothing is the worst
  outcome this codebase has a word for.
- **No elapsed-time read.** `Draw` is pure and has only `surface.Now`, so anything the reference
  animates is expressible only as a function of that instant. Item 2 of the loop brief.
- **No 3D surface.** A unit gets 2D primitives and `Projection3`. Item 3 of the loop brief.
- **A window keeps the instrument list it opened with.** A broker connecting later fills the list for
  the next window, not the open one.

## The briefs

- [Strategies](strategies.md) — ten, one per category.
- [Visualizers](visualizers.md) — ten, one per product's selling point.
- [reference/](reference/) — where captured stills and clips go, and what to capture.
