---
name: skill-author
description: How to author, improve, and trigger-tune skills for DaxAlgo Terminal's .Codex/skills/ library — frontmatter contract, "pushy" descriptions that actually trigger, the bespoke-vs-external decision (and the licensing rule), and how to wire a new skill to the agents that should load it. Use whenever adding a new skill, fixing one that won't trigger, or deciding whether to vendor an external skill. Make sure to use this skill whenever the user mentions writing/adding/editing a skill, a SKILL.md, agent capabilities, or "teach Codex to do X repeatably."
---

# Skill author — how we grow the skill library

The recipe for adding a skill to DaxAlgo Terminal. Clean-room adaptation of the public
skill-authoring workflow (credit: the `skill-creator` skill in `anthropics/skills`), written in
our own words and tailored to this repo's conventions. **No external skill is vendored verbatim**
— see "External skills" for why and how.

## A skill is a folder + a SKILL.md

```
.Codex/skills/<kebab-name>/
└── SKILL.md          # YAML frontmatter (name, description) + markdown body
    (+ optional bundled scripts/reference files the skill points to)
```

- **`name`** — kebab-case, matches the folder.
- **`description`** — the *only* trigger signal. Put **what it does AND when to use it** here, not
  in the body. Codex tends to *under*-trigger skills, so make the description a little **pushy**:
  name the concrete files/phrases/contexts that should fire it. Compare:
  - weak: "Conventions for the market-data pipeline."
  - strong: "…Use when adding store tables, changing ingest normalization, wiring trade-tape into
    a broker, or debugging 'no data'/'duplicate ticks'/'wrong timestamps', or touching anything
    under src/windows/Pipeline/TradingTerminal.MarketData/."
- **Body** — the recipe: terse, imperative, example-driven. The *why* only where non-obvious.

## House style for DaxAlgo skills

Match the existing skills (`navigator`, `add-strategy`, `quant-math`, …):

- Lead with a one-line statement of what the skill is for, then the recipe.
- Encode the **layer-graph constraints** the work touches — a skill that doesn't mention the graph
  where it's relevant is incomplete.
- Reference real seams/paths (`IBrokerClient`, `LiveSignalStrategyViewModelBase`,
  `Infrastructure/Backtest/Strategies/`) so the worker doesn't guess.
- Keep it ASCII-safe if any bundled script is PowerShell (Windows 5.1 mangles non-ASCII in no-BOM
  files — see `.Codex/hooks/verify-on-stop.ps1`).
- End with "what NOT to do" when there are sharp edges.

## Wire it up (a skill nobody loads is dead weight)

1. Add the skill folder + `SKILL.md`.
2. Add a row to the **agent → skill** table in `.Codex/MULTI-AGENT.md`.
3. Add a `## Load first` line to each owning agent in `.Codex/agents/<agent>.md` so the worker
   loads it automatically (that's how `quant-math` reaches the `strategies`/`tool-windows` agents).
4. If it's broadly useful, mention it in the `AGENTS.md` skills table.

## Does it trigger? (cheap eval)

You don't need a benchmark harness — sanity-check triggering by paraphrase:
- Write 3–5 prompts a user would actually type for this skill's job.
- For each, ask "does the description make this skill the obvious match?" If a prompt wouldn't
  fire it, the description is too narrow — widen it with the missing phrase/context.
- Watch for *over*-trigger too: if it would fire on unrelated prompts, tighten the "Skip when…".

## Bespoke vs external — and the licensing rule

- **Bespoke** (default): author it here, tailored to the repo. Everything in `.Codex/skills/`
  today is bespoke.
- **External**: useful skills exist in the wild (`anthropics/skills`,
  `VoltAgent/awesome-agent-skills`, plugin marketplaces via `/plugin`). **Before vendoring any of
  them into this repo, check the license.** If it's MIT/Apache/CC-BY, you may copy with attribution
  in the SKILL.md. **If there's no license** (e.g. `anthropics/skills` is `license: null` =
  all-rights-reserved) **or a copyleft one (AGPL/GPL), do NOT copy the file** — clean-room it:
  read it for the *ideas*, then write our own in our own words and credit the source (the same rule
  this repo applies to the Fincept review). This skill itself is an example of that.

### How to pull an external skill (mechanism)

```powershell
# Browse/install plugin bundles (skills + agents) from a marketplace:
#   in Codex:  /plugin   -> add marketplace -> install
# Or vendor a single license-clean skill by hand:
git clone --depth 1 <repo> $env:TEMP\ext-skill
#   read its LICENSE; if permissive, copy the one skill folder + keep its attribution:
Copy-Item $env:TEMP\ext-skill\skills\<name> .Codex\skills\<name> -Recurse
#   then add the agent->skill wiring above. If NOT license-clean: clean-room instead.
```

## What NOT to do

- Don't put "when to use" guidance in the body — it must live in `description` to trigger.
- Don't ship a skill without wiring it to an agent or the AGENTS.md table.
- Don't vendor unlicensed/copyleft skills — clean-room and credit.
- Don't write a 500-line skill; if it's that big, it's probably two skills.
