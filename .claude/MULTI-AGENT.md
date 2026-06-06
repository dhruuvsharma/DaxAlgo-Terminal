# Multi-agent workflow & live monitoring

DaxAlgo Terminal ships a **per-project agent fleet**: one implementer agent per
`src/TradingTerminal.*` project, each scoped to that project's ownership, dependency
constraints, and conventions — and each knows which **skill** to load first. You prompt
the main thread; it fans work out to the matching project agent; the agent loads its
skill and does the change in its own context, keeping the main thread lean.

This is already wired. Nothing to "turn on" in the repo. What this doc covers:
how the fleet is organized, and how to **watch agents work live**.

## The fleet (36 agents)

Routing index lives in [`agents/README.md`](agents/README.md). Summary:

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

### Each agent → its skill

The agent does project-scoped *ownership + guardrails*; the skill carries the *recipe*.
The agent loads the skill so you don't have to name it.

| Agent(s) | Skill loaded first |
|---|---|
| `infrastructure` | `broker-gotchas` (+ `backtest-engine`); IB → defer to `ib-api-expert` |
| `market-data` | `market-data-pipeline` (+ `archive-offloader`) |
| `ui-shared` | `wpf-mvvm-rules` (binding/theme → `xaml-fixer`) |
| `backtest-cli`, `backtest-tool` | `backtest-engine` |
| `strat-orderflowcube`, `strat-orderflowsurfacespike`, `strat-imbalanceheatfront`, `strat-indexkscoresurface` | `regime-cube-strategy` → `add-strategy` |
| other `strat-*` | `add-strategy` |
| `ai-*` (4 windows + `ai-seam`) | `ai-analyst` |
| `app-shell` | `navigator` |
| `core-domain`, tool windows | inline conventions (no single skill) |

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

1. You prompt the main thread ("change the Charts symbol picker", "wire a 5th broker").
2. Main thread routes to the owning agent (`charts`, or `add-broker` recipe via
   `infrastructure`), which loads its skill.
3. For parallel work, agents are launched in the background → watch them in
   `claude agents` / Tasks pane.
4. Each agent reports back; main thread integrates and runs `dotnet build` / `dotnet test`.

## Token note

All 36 agent descriptions load into every session's system prompt. If that baseline ever
feels heavy, collapse rarely-touched ones (e.g. fold the 9 `strat-*` into one
`strategies` agent) — they're just files in [`agents/`](agents/).
