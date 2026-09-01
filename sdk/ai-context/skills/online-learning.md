---
id: online-learning
name: Learning as the data arrives, and knowing whether it worked
triggers: predict, prediction, forecast, forecasting, learn, learning, train, training, model, ml, machine learning, classifier, classification, probability, likelihood, regression, rls, sgd, gradient descent, logistic, calibration, brier, accuracy, hit rate, adaptive, online learning, anticipate, expected
---

# Learning as the data arrives

A unit that predicts something — the next move, whether a wall holds, whether the spread is about to
widen — has **no dataset**. It sees one observation at a time and must fold each one in as it arrives.
Everything here does that in place, in O(d) or O(d²) per sample, deterministically and on one thread.

`DaxAlgo.Sdk.Quant` is ambient; no `using` is needed.

## Picking the learner

| You want | Use |
|---|---|
| A continuous target, adapting to a changing regime | `OnlineLinearRegression` — RLS; `lambda` < 1 forgets, 0.99 is the usual "slowly adapt" |
| The same, cheaper and higher-variance | `OnlineGradientDescent` — first-order, O(d) |
| A probability of an event | `OnlineLogisticRegression` — cannot leave [0, 1], and calibrates near the extremes |
| Features on comparable scales, first | `OnlineFeatureScaler` — RLS on raw inputs is numerically fragile |
| "Is my forecast any good" | `RollingForecastMetrics` (error and direction), `RollingBrierScore` (probabilities) |

Every learner exposes `Predict`, `Update`, `Samples`, and `SaveState`/`LoadState` — the last pair is
the warm start, so a window reopened mid-session resumes rather than relearning from nothing.

## The three rules that decide whether any of it means anything

**Score before you learn, never after.** Predict the next observation, record the score, *then* update
on that sample. Reversed, you are measuring a model that has already been shown the answer: the
read-out looks excellent and the live forecast is worthless. This is the single easiest way to ship a
unit that lies to its user.

```csharp
// on each new observation, in this order
var predicted = _learner.Predict(features);         // 1. predict
_metrics.Score(predicted, realised);                // 2. score the PREVIOUS answer
_learner.Update(features, realised);                // 3. only now, learn
```

**Report against a baseline or not at all.** A Brier score of 0.2 means nothing until you know that
always predicting the base rate scores `r(1−r)` — which is why `RollingBrierScore` returns `BaseRate`
beside it. For a continuous target the baseline is "no change"; run a second metrics window on that
and show both. A model that cannot beat "tomorrow looks like today" is worth saying so about.

**Standardise, and keep the bias out of it.** `OnlineFeatureScaler` transforms each dimension to
`(x − μ)/σ` with a decay and clamps outliers, but passes the leading `passthroughDimensions` through
untouched: standardising a constant zeroes it, which silently removes the intercept from every
learner downstream.

## Where it goes in a unit

The learner lives in a field and is updated in the **data callback**, never in `Draw` — training is
exactly the per-frame work `Draw` must not do, and `Draw` may run more than once per frame, so a
learner updated there would see every sample twice.

Show the accuracy on screen beside the prediction. A forecast with no visible score is a number the
viewer has no way to distrust, and `Tiles.Draw` fed from a metrics snapshot is one call.

**Gate on `Samples`.** A learner's first predictions are not a rough version of its converged ones;
they are noise of the same shape. Draw "learning…" until it has enough, exactly as an estimator's
`IsReady` gate works.
