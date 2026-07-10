# PROTOCOL — the per-change contract (token-saving, mandatory)

Every change request follows this sequence. The goal: **signatures over source, grep over read,
narrowest build, smallest diff.** The context layer (`index.md`, `symbols.md`, `deps.json`) exists
so steps 2–4 cost hundreds of tokens, not tens of thousands.

## Sequence

1. **ACKNOWLEDGE** in one sentence.
2. **LOAD context first**: `.claude/context/index.md` + `symbols.md` + `deps.json`. These are small
   by design. Do NOT open source files yet. (Per-file rows: grep `index/`; full surfaces: grep `symbols/`.)
3. **Resolve symbols from the layer**: if the API is in `symbols/` → form the hypothesis from
   signatures alone. If not → `rg -n` to find the line, then read ±20 lines around it
   (`sed -n 'A,Bp'` / ranged Read). **NEVER read whole a file the index marks > 200 LOC.**
4. **BLAST RADIUS** from `deps.json`: name the affected modules, editions, trees explicitly.
5. **ASK up to 5 clarifying questions** if scope / behavior / edge cases / backward-compat /
   error semantics are unclear. WAIT. Never guess.
6. **CREATE** `tasks/<YYYY-MM-DD-HHMM>-<slug>.md`: Goal · Hypothesis (affected files) ·
   Qs+answers · Plan (ordered) · Blast radius · Build filter · Tests to run · (at end) diff summary.
7. **CONFIRM the plan in ≤10 bullets. WAIT for "go".** (Skip the wait only when the user
   pre-authorized the change in the same message.)
8. **ROUTE**: 3+ projects AND parallelizable → manager spine (manager → workers → build-runner →
   verifier). Otherwise → inline on the main thread, loading the matching skill yourself.
   Single known-path lookup → Read/Grep directly, no agent. **No subagent for < 3-file changes.**
9. **EDIT smallest diff.** Prefer Edit over Write. No unrelated reformatting. Don't re-read a file
   you just edited — cache findings in the task scratchpad.
10. **BUILD the narrowest filter** that covers the change:
    `dotnet build TradingTerminal.Windows.Intermediate.slnf` (default dev edition) or `.Basic.slnf`;
    the full `TradingTerminal.Windows.slnx` only when the change is cross-cutting (Core/MarketData/
    Infrastructure/UI.Core signatures) or touches both shells. Never bare `dotnet build`.
11. **TEST the affected area only**: `dotnet test tests/TradingTerminal.Tests.Headless --filter
    "FullyQualifiedName~<Area>"` (add `tests/TradingTerminal.Tests` when WPF-touching).
12. **Multiply where required** (see RECIPES/):
    - shell code → ALL 3 copies: `App.Basic` + `App.Intermediate` here, note the Pro-repo copy for Dhruv.
    - backend fix that applies to both trees → `src/windows/` AND `src/linux/` (confirm scope first).
13. **Stop hooks** (`build-on-stop` → `verify-on-stop` → `leakcheck-on-stop` → `docsync-on-stop`)
    gate the turn automatically — don't duplicate their work manually.
14. **UPDATE the layer + trackers**: regenerate/patch `symbols/` if public API changed; `index/`
    if files added/removed; `deps.json` if references changed; the touched `modules/` doc;
    append `changelog.md`; tick the GitHub issue checkbox + commit hash (issues are the source of truth).
15. **REPORT**: files changed, +/− lines, tests run + result, risks, deferred items.

## Hard stops (stop and report; do not push through)

- Build broken and not fixed after **2** attempts.
- Symbol not found after **3** rg attempts → ask.
- Change unexpectedly touches **> 5 files** → re-plan with Dhruv.
- Change would cross the **open-core boundary** (Pro → public) → STOP; never copy Pro code here.
- Change touches **both trees** when only one was authorized → confirm before editing the second.

## Standing invariants (checked by hooks, restated for planning)

Core depends on nothing · MarketData below Infrastructure · no SDK types above Infrastructure ·
strategies/brokers via IStrategyFactory/IBrokerSelector, never `new` · VMs subscribe to the hub,
not brokers · tick-primary (no live bars to store) · provenance never stripped · one Activity Log ·
strict MVVM · `AddCredentialedLoginForms()` only ever paired with `AddCredentialedBrokers()` ·
data/signals only (no live order execution) · sidecar binds 127.0.0.1 only · no `--no-verify`.
