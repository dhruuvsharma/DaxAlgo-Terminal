# AI Strategy Builder

> Last updated: 2026-07-18

Describe a strategy in plain English and have it written, compiled, and auto-fixed for you — in the app
or from the command line. This page covers what runs where, what leaves your machine, and what the
generated code can and can't do.

## Three ways to use it

| Tier | Where | Output |
|---|---|---|
| **Vibe Quant** (in-app) | Strategy Studio → Vibe Quant — an agent workspace: session rail, a transcript that shows the build plan, tool cards and per-file change chips as the agent works, and a workbench with the code editor | A working strategy (kernel + catalog descriptor + live view-model) in the workbench, compiled and ready to review — the host composes its live window |
| **`dotnet new` + your own agent** | `dotnet new daxalgo-strategy` then open Claude Code / Codex / Cursor in the folder | A full plugin — the scaffold's `CLAUDE.md`/`AGENTS.md` carry the same context pack |
| **`daxalgo strategy ai` CLI** | The `daxalgo` global tool | A full plugin: scaffold → AI-write the kernel → build → test → package |

All three share one **generated context pack** (`sdk/ai-context/`, embedded in the app and the CLI) —
the SDK contract, the hard rules, and the output format — so they teach a model the same thing.

## Providers — you bring the access

"Log in with my ChatGPT/Claude account" can't be embedded in a third-party app; consumer-subscription
auth isn't offered to external apps. So the builder uses paths that are legitimate:

- **Installed agent CLIs** — Claude Code (`claude`), OpenAI Codex (`codex`). Their login lives in the
  vendor's own tool; the builder drives them headless and **never sees your credentials**. Available
  when the CLI is on your PATH.
- **BYO API keys** — OpenAI, DeepSeek, xAI (Grok), OpenRouter (one OpenAI-compatible client), and
  Anthropic (native). Keys live in the credential store, never in config.
- **Local Ollama** — offline, no key. (Small local models often can't write a whole kernel — hosted
  providers are recommended for v1.)

The provider picker shows every provider; the ones that aren't set up read "not set up". With none
configured, the pane offers setup guidance instead of erroring.

**Setting up providers.** Open **Settings → AI providers**. Installed agent CLIs and local Ollama show as
detected (no key needed); for the keyed providers, paste an API key and Save — it's stored **encrypted
for your Windows user (DPAPI)**, never in config. A `{PROVIDER}_API_KEY` environment variable works too
(and is shared with the CLI). Restart to apply a new key.

## What leaves your machine

Only your prompt and the context pack, sent to the provider you pick. Everything else — compiling,
the auto-fix loop, the backtest — is local. There is no telemetry.

## Generated code is untrusted — and gated

The model's output is code you didn't write, running in-process. It goes through the **same gate as any
plugin**:

- **The policy scan is in the compile step.** A generated strategy that P/Invokes, starts a process, or
  otherwise trips the scan **fails to compile** — it can never reach your catalog. This isn't a separate
  check you can skip; it's the compiler everyone uses.
- **You review before you save — through a diff.** In the app, a turn puts the code in the workbench
  and shows the diagnostics but does **not** register it. **Compile & Register** compiles and then opens
  a review overlay: per-file line diffs against what was last registered, the warning count, and an
  explicit **Register** button. Registration only happens from inside that review — there is no path
  around it, and backing out registers nothing.
- **The DEV (unsigned) badge.** An AI-authored strategy is unsigned by definition and wears the
  **DEV (unsigned)** badge in the catalog, the same as any authored or unsigned-plugin strategy. See
  [plugin-security.md](plugin-security.md).

## The live window — composed by default

The model writes three things: the **kernel** (the strategy math), the **catalog descriptor**, and the
**live view-model**. It deliberately does *not* write a view — Roslyn can't compile XAML, and a
hand-rolled code-built view is worse than what the host already has. Instead, the host **composes the
live window from the descriptor's `DataRequirement`**:

| Declared flag | What the window gets |
|---|---|
| `Bars` | The price chart (1-minute candles + indicators) |
| `Depth` | The Order Book panel — depth ladder, microstructure strip, liquidity heatmap |
| `TradeTape` | The Volume Footprint panel — bid×ask clusters, imbalances, value area |
| `L1` only | A live quote card |

…plus the shared strategy chrome: the setup form (instrument picker), Start/Stop and arm controls,
pause/presets/CSV/snapshot, and the signal feed the kernel emits into. The panels are the same code the
standalone chart tools run, in their *Embedded* presets: **no toolbar** (the strategy window owns the
instrument) and **no ML forecasters** (never constructed, not merely hidden — an embedded panel costs
only what it shows). Composed strategies survive restarts: the persisted plugin re-registers the same
composed window through the normal plugin loader.

A model (or you) can still write a custom view — a code-built WPF `UserControl` in the same file set —
and the host will use it instead. That's the escape hatch for bespoke UI, not the default.

## The CLI

```
daxalgo strategy <action> [options]

  new      --name <N> [--ui] [--output <dir>]
  build    [--project <dir>]
  test     [--project <dir>]
  package  [--project <dir>]                       # -> <N>.daxplugin
  install  --into <plugins-dir> [--project <dir>]
  ai       --name <N> [--provider <id>] [--prompt "…"] [--ui] [--max-attempts <n>]
```

`ai` scaffolds (if needed), asks the provider for the kernel, writes it, builds, feeds build errors back
and retries, then tests and packages. `--provider fake` keeps the scaffolded kernel unchanged — the
plumbing check CI runs. Real providers read their key from a `{PROVIDER}_API_KEY` environment variable;
agent CLIs use their own login.

Install the tool: `dotnet tool install -g DaxAlgo.StrategyTool` (needs the template:
`dotnet new install DaxAlgo.Templates`).

See also: [plugin-authoring.md](plugin-authoring.md) · [plugins.md](plugins.md) ·
[plugin-security.md](plugin-security.md).
