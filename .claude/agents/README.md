# Project agents — routing index

One implementer agent per solution project (you asked for the full per-project set).
Each agent owns exactly one `src/TradingTerminal.*` project: it knows that project's
ownership, dependency constraints (the solution graph it must not break), conventions,
and which skill to load first. Give a prompt naming the area and the matching agent does
the change in its own context — keeping the main thread's context lean.

> **Token note:** all 30 descriptions load into the system prompt every session. If the
> baseline cost feels heavy, collapse the rarely-touched ones (e.g. fold the 9 `strat-*`
> into one `strategies` agent) — they're just files in this folder.

## Foundational layer
| Project | Agent | Model |
|---|---|---|
| Core | `core-domain` | opus |
| MarketData | `market-data` | opus |
| Infrastructure | `infrastructure` | opus |
| UI | `ui-shared` | sonnet |
| Login | `login` | sonnet |
| Ai (seam) | `ai-seam` | sonnet |
| App (shell) | `app-shell` | opus |
| Backtest.Cli | `backtest-cli` | sonnet |

## AI tool windows
| Project | Agent | Model |
|---|---|---|
| Ai.MarketAnalyst | `ai-marketanalyst` | sonnet |
| Ai.FactorResearch | `ai-factorresearch` | sonnet |
| Ai.MlFeatures | `ai-mlfeatures` | sonnet |
| Ai.BacktestAnalysis | `ai-backtestanalysis` | sonnet |

## Tool windows
| Project | Agent | Model |
|---|---|---|
| Charts | `charts` | sonnet |
| OrderBook | `orderbook` | sonnet |
| VolumeFootprint | `volumefootprint` | sonnet |
| Correlation | `correlation` | sonnet |
| MarketRegime | `marketregime` | sonnet |
| InstrumentRegime | `instrumentregime` | sonnet |
| MarkovRegime | `markovregime` | sonnet |
| Backtest (tab) | `backtest-tool` | sonnet |
| Recording | `recording` | sonnet |

## Strategy windows
| Project | Agent | Model |
|---|---|---|
| Strategies.ApexScalper | `strat-apexscalper` | sonnet |
| Strategies.CumulativeDelta | `strat-cumulativedelta` | sonnet |
| Strategies.ImbalanceHeatFront | `strat-imbalanceheatfront` | sonnet |
| Strategies.IndexKScoreSurface | `strat-indexkscoresurface` | opus |
| Strategies.OrderFlowCube | `strat-orderflowcube` | opus |
| Strategies.OrderFlowSurfaceSpike | `strat-orderflowsurfacespike` | opus |
| Strategies.OrderFlowToxicity | `strat-orderflowtoxicity` | sonnet |
| Strategies.OrnsteinUhlenbeck | `strat-ornsteinuhlenbeck` | sonnet |
| Strategies.VolatilityTargeted | `strat-volatilitytargeted` | sonnet |

## Pre-existing specialist agents (not project-scoped)
`ib-api-expert` (opus) · `xaml-fixer` (sonnet) · `wpf-explorer` (haiku) · `dotnet-reviewer` (sonnet)

**Model rationale:** Opus where a mistake ripples (Core/MarketData/Infrastructure/App) or the
work is genuinely hard (3D cube/surface strategies); Sonnet for focused window/tool/strategy
edits with a clear template — biasing toward fewer tokens per the project's routing rules.
