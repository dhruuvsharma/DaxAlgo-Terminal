# Multi-agent workflow & live monitoring

DaxAlgo Terminal ships a **lean agent fleet**: an orchestration spine over a small set of
scoped implementers, each knowing its projects' ownership, dependency constraints, and which
**skill** to load first. You prompt the main thread; it works **inline by default** and fans
out to agents only when the work genuinely warrants it (see "Token discipline" in
[`agents/README.md`](agents/README.md)).

This is already wired. Nothing to "turn on" in the repo. What this doc covers:
how the fleet is organized, and how to **watch agents work live**.

## The fleet (19 agents)

Routing index lives in [`agents/README.md`](agents/README.md). Summary:

- **3 orchestration** — `manager` (lead/architect planner), `build-runner` (build+test gate),
  `verifier` (plan-aware watcher). These sit *above* the workers; see "The spine" below.
- **8 foundational** — `core-domain`, `market-data`, `infrastructure`, `ui-shared`,
  `login`, `ai-seam`, `app-shell`, `backtest-cli`.
- **3 consolidated window owners** — `strategies` (all 10 `Strategies.*` live windows),
  `tool-windows` (all 10 tool/chart windows), `ai-windows` (all 4 `Ai.*` windows).
  Per-project quirks live in tables inside each agent body.
- **5 specialists** — `ib-api-expert`, `xaml-fixer`, `wpf-explorer`, `dotnet-reviewer`, and
  `paper-repro` (the Paper Lab research-reproduction subsystem — `Core/Research/` +
  `Infrastructure/Research/` + the untrusted-code sandbox + sidecar repro endpoints).

> This was previously a 39-agent per-project fleet. All descriptions load into the system
> prompt on every request, and per-window agents pulled routine one-file edits onto the
> expensive cold-start path — so the 24 single-window agents were folded into the three
> consolidated owners above.

## The spine (manager → workers → build → verify)

**The spine is opt-in, not the default.** Use it only for features spanning **3+ projects**
with parallelizable parts. Single-project and 1–2-file changes are done inline on the main
thread (load the skill yourself); the Stop hooks gate the turn — no manager, build-runner,
or verifier spawns. Each subagent starts cold and re-derives context, so the spine roughly
multiplies token cost by the number of agents involved — it must buy parallelism or context
isolation to be worth it.

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
| `ui-shared` | `wpf-mvvm-rules` (binding/theme → `xaml-fixer`); `memory-safety` for the streaming bases |
| `backtest-cli` | `backtest-engine` |
| `strategies` | `memory-safety` (every live window), plus per-strategy table in its body: `add-strategy`, plus `regime-cube-strategy`/`quant-math` for cube/surface/OU/VPIN work |
| `tool-windows` | `memory-safety` (every streaming/render window), plus per-project table in its body: `quant-math` (Correlation/MarkovRegime), `backtest-engine` (Backtest window) |
| `ai-windows`, `ai-seam` | `ai-analyst`; `memory-safety` for streaming/polling windows; `paper-reproduction` for the Paper Lab window |
| `app-shell` | `navigator` |
| `paper-repro` | `paper-reproduction` + `untrusted-execution` (+ `paper-ingestion`); `backtest-engine` for the bridge, `quant-math` for signal semantics |
| `build-runner`, `verifier` | (own instructions; `verifier` is plan-aware) |
| `core-domain` | inline conventions (no single skill) |

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

**Small change** (one project, 1–2 files): the main thread does it **inline** — loads the
relevant skill, edits, runs `dotnet build` itself. No agents spawned; the Stop hooks are the
gate. This is the common case and the cheap path.

**Single-project but heavy** (big exploration, or IB/XAML specialist territory): one worker
(`strategies` / `tool-windows` / `ai-windows` / a foundational agent / `ib-api-expert` /
`xaml-fixer`), then inline `dotnet build`. Still no spine.

**Multi-project change** (3+ projects, parallelizable):
1. You prompt the main thread ("wire a 5th broker", "add a vol-of-vol regime cube").
2. Main thread invokes **`manager`** → gets an Execution Plan (tasks, owners, skills, sequence,
   **with file paths + findings embedded** so workers don't re-explore).
3. Main thread dispatches each task to its worker; independent tasks run backgrounded → watch
   them in `claude agents` / the Tasks pane.
4. Workers report back; main thread invokes **`build-runner`** (build+test) then **`verifier`**
   (given the plan) for the punch list.
5. On Stop, `build-on-stop` + `verify-on-stop` hooks gate the turn — a hard block on compile
   errors, layer-graph violations, or SDK leaks.

## Token note

Every agent description loads into the system prompt on every request — that's why the fleet
is 19 agents, not 39. Keep descriptions tight, keep per-project detail in agent **bodies**
(loaded only on spawn), and don't add a new agent when a row in an existing consolidated
agent's table will do. The full spawn-vs-inline rules live in
[`agents/README.md`](agents/README.md) ("Token discipline").
