# Contributing to DaxAlgo Terminal

Thanks for helping out! This page is the **environment onboarding** — how to get a working
dev setup, including Claude Code with the project's AI config. For the **code conventions**
(layering rules, MVVM, how to add a strategy/broker/notifier), see
[docs/contributing.md](docs/contributing.md) and [CLAUDE.md](CLAUDE.md).

## TL;DR

```powershell
git clone https://github.com/dhruuvsharma/DaxAlgo-Terminal.git
cd "DaxAlgo Terminal"
scripts\setup-claude-code.bat   # one-time: installs Git, .NET 9 SDK, Claude Code
# reopen your terminal, then:
claude-here.bat                 # launches Claude Code in the project
```

## Prerequisites

| Tool | Why | Notes |
|---|---|---|
| **Windows 10/11** | The app is `net9.0-windows` (WPF) | The helper `.bat` files and the project hooks are Windows-only. |
| **Git** | Source control + Claude Code's Bash tool | `setup-claude-code.bat` installs it via winget if missing. |
| **.NET 9 SDK** | Build / run the terminal | Must be a **9.x** SDK (bundles the matching desktop runtime). |
| **Claude Code** | The AI dev CLI | Installed by the setup script (native build, auto-updates). |
| **A paid Claude plan** | Required to *use* Claude Code | Pro / Max / Team / Enterprise, or an API provider key. **Not** on the free claude.ai tier. Each contributor signs in with their own account. |

No broker account is needed to build or run — connect the **Binance** tile (real, keyless
crypto data) or the offline **Simulated** broker. See [docs/getting-started.md](docs/getting-started.md).

## The Claude Code setup is already in the repo

You don't install agents, skills, or plugins separately — they're **committed to the repo** and
load automatically when you open Claude Code in this folder:

- **`CLAUDE.md`** — the always-loaded project guide (architecture, rules, do/don't).
- **`.claude/agents/`** — 38 subagents: a 3-agent orchestration tier (`manager`/`build-runner`/`verifier`) over per-area implementer + specialist agents (see [`.claude/agents/README.md`](.claude/agents/README.md)).
- **`.claude/skills/`** — 14 lazy-loaded skills (`navigator`, `add-strategy`, `software-architecture`, `quant-math`, …).
- **`.claude/MULTI-AGENT.md`** — "the spine": how the manager plans → workers build → build-runner + verifier gate.
- **`.claude/hooks/`** — session-start orientation + a build/doc-sync check on stop (PowerShell).
- **`.claude/MULTI-AGENT.md`** — how the multi-agent workflow is meant to be driven.

`scripts/setup-claude-code.bat` installs the *tooling* (Git, .NET 9 SDK, Claude Code, and checks for
PowerShell, which the hooks need), then **verifies** the committed `.claude/` workflow and reports what's
wired (`✓ 38 agents, ✓ 14 skills, ✓ 4 hooks, ✓ settings.json/MULTI-AGENT.md/CLAUDE.md`). Cloning the repo
is what *delivers* the AI config — the script never copies it into your global `~/.claude/` (a global copy
would drift from the repo and break the agents' repo-relative paths). `claude-here.bat` just `cd`s to the
repo root and runs `claude` so that project-scoped config is picked up automatically.

> **Personal vs shared settings.** `.claude/settings.json` is the **shared** project config (checked
> in) and ships **conservative defaults** — `acceptEdits` mode (edits auto-apply; they're
> git-reviewable) with a read-only command allowlist, so a fresh clone never runs shell commands
> without a prompt. Anything personal — a bypass/no-prompt mode, a wider command allowlist, your
> model choice — belongs in `.claude/settings.local.json`, which is git-ignored and never shipped to
> others.

## Workflow

1. Branch off `main`.
2. Make your change. Keep the layering rules in [CLAUDE.md](CLAUDE.md) intact (`Core` depends on
   nothing; brokers/strategies are plug-ins behind their seams; strict MVVM).
3. `dotnet build` and `dotnet test` must be green.
4. Open a PR. The repo's stop-hook reminds you to update docs when project structure changes.

For the deeper "how do I add X" recipes, ask Claude to load the matching skill (e.g.
`/add-strategy`) or read [docs/contributing.md](docs/contributing.md).
