# AI context pack

`daxalgo-strategy-context.md` is the **generated system prompt** that teaches an LLM to write a correct
DaxAlgo Terminal strategy — the SDK contract, the hard rules, a worked example, and two output contracts
(a single-file kernel for the in-app AI pane, a full plugin project for the template / CLI).

It is **generated, not hand-maintained**, so it can't drift from the code:

```powershell
pwsh build/gen-ai-context.ps1     # from the repo root; regenerates daxalgo-strategy-context.md
```

The drift-prone pieces — the SDK version and the worked-example kernel — are read from source
(`SdkInfo.cs`, the template kernel); the rest is canonical guidance in the generator. Output is
byte-stable across runs (the only stamp is `SdkInfo.Version`), so CI diffs the committed pack against a
fresh generation and fails if they differ. **Don't hand-edit the `.md`** — change a source or the
generator and regenerate.

## Consumers (issue #26)

- **In-app AI builder pane** — the system prompt (output contract *a*).
- **`dotnet new daxalgo-strategy`** — the scaffold's `CLAUDE.md`/`AGENTS.md` (output contract *b*).
- **`daxalgo strategy ai` CLI** — the orchestrator's system prompt.

Everything the pack drives is local except the prompt + pack sent to the user's chosen provider.
