# Run — <date> · <label>

**Config** model `<id>` · effort `<Standard|Deep|Max>` · provider `<openrouter|tokenrouter|nvidia>`
**Prompt pack** `sdk/ai-context/` at commit `<sha>`
**Ran by** <who> · **Scored by** <who>

Baseline this is compared against: `<runs/…md>` — or *"none, this is the baseline"*.

## Scores

| # | Brief | Tier | R | D | M | L | V | I | S | /21 | s | gens | Note |
|---|-------|------|---|---|---|---|---|---|---|-----|---|------|------|
| S1 | Indicator confluence | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S1 | Indicator confluence | spec |  |  |  |  |  |  |  |  |  |  |  |
| S2 | Candlestick reversal | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S2 | Candlestick reversal | spec |  |  |  |  |  |  |  |  |  |  |  |
| S3 | Opening range breakout | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S3 | Opening range breakout | spec |  |  |  |  |  |  |  |  |  |  |  |
| S4 | Anchored VWAP bands | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S4 | Anchored VWAP bands | spec |  |  |  |  |  |  |  |  |  |  |  |
| S5 | Smart money / ICT | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S5 | Smart money / ICT | spec |  |  |  |  |  |  |  |  |  |  |  |
| S6 | Queue imbalance scalper | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S6 | Queue imbalance scalper | spec |  |  |  |  |  |  |  |  |  |  |  |
| S7 | Stacked-imbalance absorption | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S7 | Stacked-imbalance absorption | spec |  |  |  |  |  |  |  |  |  |  |  |
| S8 | Toxicity gate | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S8 | Toxicity gate | spec |  |  |  |  |  |  |  |  |  |  |  |
| S9 | Liquidity wall absorption | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S9 | Liquidity wall absorption | spec |  |  |  |  |  |  |  |  |  |  |  |
| S10 | Value-area breakout | ask  |  |  |  |  |  |  |  |  |  |  |  |
| S10 | Value-area breakout | spec |  |  |  |  |  |  |  |  |  |  |  |
| V1 | The chart | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V1 | The chart | spec |  |  |  |  |  |  |  |  |  |  |  |
| V2 | Liquidity heatmap | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V2 | Liquidity heatmap | spec |  |  |  |  |  |  |  |  |  |  |  |
| V3 | Volumetric bars | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V3 | Volumetric bars | spec |  |  |  |  |  |  |  |  |  |  |  |
| V4 | Depth and sales ladder | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V4 | Depth and sales ladder | spec |  |  |  |  |  |  |  |  |  |  |  |
| V5 | Market profile | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V5 | Market profile | spec |  |  |  |  |  |  |  |  |  |  |  |
| V6 | Smart tape | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V6 | Smart tape | spec |  |  |  |  |  |  |  |  |  |  |  |
| V7 | Cumulative delta | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V7 | Cumulative delta | spec |  |  |  |  |  |  |  |  |  |  |  |
| V8 | Depth chart | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V8 | Depth chart | spec |  |  |  |  |  |  |  |  |  |  |  |
| V9 | Gamma levels (probe) | ask  |  |  | — | — | — | — |  |  |  |  |  |
| V10 | Strategy tearsheet | ask  |  |  |  |  |  |  |  |  |  |  |  |
| V10 | Strategy tearsheet | spec |  |  |  |  |  |  |  |  |  |  |  |

**Fidelity mean** (excluding V9): ask `__ / 21` · spec `__ / 21`
**Ran at all** (R > 0): `__ / 39`

## Deductions

One line per point lost, tagged with the half that owns it.

```
S1 ask  L-1 harness: …
S1 ask  I-2 product: …  -> authored-unit-gaps.md
```

## What this run says

Three or four sentences. What moved since the baseline, what did not, and the one cause worth fixing
next. Not a list — the list is above.
