# Multi-agent workflow & live monitoring

DaxAlgo Terminal ships a **per-project agent fleet**: one implementer agent per
`src/TradingTerminal.*` project, each scoped to that project's ownership, dependency
constraints, and conventions — and each knows which **skill** to load first. You prompt
the main thread; it fans work out to the matching project agent; the agent loads its
skill and does the change in its own context, keeping the main thread lean.

This is already wired. Nothing to "turn on" in the repo. What this doc covers:
how the fleet is organized, and how to **watch agents work live**.

## The fleet (38 agents)

Routing index lives in [`agents/README.md`](agents/README.md). Summary:

- **3 orchestration** — `manager` (lead/architect planner), `build-runner` (build+test gate),
  `verifier` (plan-aware watcher). These sit *above* the workers; see "The spine" below.
- **8 foundational** — `core-domain`, `market-data`, `infrastructure`, `ui-shared`,
  `login`, `ai-seam`, `app-shell`, `backtest-cli`.
- **4 AI windows** — `ai-marketanalyst`, `ai-factorresearch`, `ai-mlfeatures`,
  `ai-backtestanalysis`.
- **10 tool windows** — `charts`, `orderbook`, `volumefootprint`, `heatmap`,
  `correlation`, `marketregime`, `instrumentregime`, `markovregime`, `backtest-tool`,
  `recording`.
- **9 strategy windows** — `strat-apexscalper`, `strat-cumulativedelta`,
  `strat-imbalanceheatfront`, `strat-indexkscoresurface`, `strat-orderflowcube`,
  `strat-orderflowsurfacespike`, `strat-orderflowtoxicity`, `strat-ornsteinuhlenbeck`,
  `strat-volatilitytargeted`.
- **4 specialists** — `ib-api-expert`, `xaml-fixer`, `wpf-explorer`, `dotnet-reviewer`.

## The spine (manager → workers → build → verify)

The realization of the "company of agents". Claude Code spawns **one level deep** — a subagent
can't spawn its own subagents — so the **main thread is the general contractor** and the manager
hands back a plan rather than dispatching workers itself:

```
prompt
  │
  ▼  invoke `manager` (read-only)  ──►  returns an Execution Plan
  │
main thread dispatches each plan task to its worker agent (parallel where independent)
  │
  ▼  invoke `build-runner`  ──►  dotnet build + test, pass/fail
  │
  ▼  invoke `verifier` (given the plan)  ──►  BLOCKER/NIT punch list
  │
  ▼  Stop → `build-on-stop` + `verify-on-stop` hooks gate the turn (hard block on
            compile errors / layer-graph / SDK leaks)
```

- **`manager`** loads `software-architecture` (decomposition, the design-pattern catalog, SOLID
  checks, the plan contract). It is the only agent that holds the whole-project picture.
- **Workers** are the per-project agents below; each loads its own skill.
- **`build-runner`** then **`verifier`** are the gate. The `verify-on-stop.ps1` hook enforces the
  deterministic subset even if the agent step is skipped.

### Each agent → its skill

The agent does project-scoped *ownership + guardrails*; the skill carries the *recipe*.
The agent loads the skill so you don't have to name it.

| Agent(s) | Skill loaded first |
|---|---|
| `manager` | `software-architecture` (+ `navigator`; skim `quant-math` for quant routing) |
| `infrastructure` | `broker-gotchas` (+ `backtest-engine`); IB → defer to `ib-api-expert` |
| `market-data` | `market-data-pipeline` (+ `archive-offloader`) |
| `ui-shared` | `wpf-mvvm-rules` (binding/theme → `xaml-fixer`) |
| `backtest-cli`, `backtest-tool` | `backtest-engine` |
| `strat-orderflowcube`, `strat-orderflowsurfacespike`, `strat-indexkscoresurface` | `regime-cube-strategy` + `quant-math` → `add-strategy` |
| `strat-imbalanceheatfront` | `regime-cube-strategy` → `add-strategy` |
| `strat-ornsteinuhlenbeck`, `strat-orderflowtoxicity` | `quant-math` → `add-strategy` |
| other `strat-*` | `add-strategy` (+ `quant-math` where it touches stats/geometry) |
| `correlation`, `markovregime` | `quant-math` |
| `ai-*` (4 windows + `ai-seam`) | `ai-analyst` |
| `app-shell` | `navigator` |
| `build-runner`, `verifier` | (own instructions; `verifier` is plan-aware) |
| `core-domain`, other tool windows | inline conventions (no single skill) |

## Watching agents work live

> Heads up: there is **no "FleetView"** feature. The real mechanisms below are what give
> you the split / grouped live view of concurrent agents.

### CLI — `claude agents` (Agent View)
A unified screen showing every background session, grouped by **Needs input / Working /
Completed**, with live state indicators. You can peek at an individual session's output
or attach to a running one, and dispatch new agents from the view. This is the closest
thing to "watch every agent at once."

### Background a running subagent — `Ctrl+B`
By default a spawned subagent runs in the foreground and blocks the conversation. Press
**Ctrl+B** while it runs to background it; it keeps going concurrently and shows up in
Agent View. Spawning several backgrounded agents = several panes of live work.

### Desktop app — Tasks pane
If you use the Claude desktop app: **Views → Tasks** shows all background subagents and
shell tasks live. Click any entry to see its output or stop it. Drag it into the layout
to keep it beside the conversation (the "split terminal" you wanted).

### Forks (optional, opt-in)
Set env var `CLAUDE_CODE_FORK_SUBAGENT=1` to spawn parallel forks of the current
conversation; each inherits full context, and a fork panel appears under the prompt
(arrow keys to switch rows, `Enter` to open a fork's transcript).

### Env toggles
- `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1` disables backgrounding (set `0` to re-enable).
- `CLAUDE_CODE_FORK_SUBAGENT=1` enables forks.

## Typical loop

**Small change** (one project): prompt the main thread naming the area → it routes to the owning
worker (`charts`, etc.), which loads its skill → `build-runner` → done. No manager needed.

**Multi-project change**: 
1. You prompt the main thread ("wire a 5th broker", "add a vol-of-vol regime cube").
2. Main thread invokes **`manager`** → gets an Execution Plan (tasks, owners, skills, sequence).
3. Main thread dispatches each task to its worker; independent tasks run backgrounded → watch
   them in `claude agents` / the Tasks pane.
4. Workers report back; main thread invokes **`build-runner`** (build+test) then **`verifier`**
   (given the plan) for the punch list.
5. On Stop, `build-on-stop` + `verify-on-stop` hooks gate the turn — a hard block on compile
   errors, layer-graph violations, or SDK leaks.

## Token note

All 38 agent descriptions load into every session's system prompt. If that baseline ever
feels heavy, collapse rarely-touched ones (e.g. fold the 9 `strat-*` into one
`strategies` agent) — they're just files in [`agents/`](agents/). The 3 orchestration agents
(`manager`/`build-runner`/`verifier`) earn their slot by keeping the main thread's context lean
on big changes.
