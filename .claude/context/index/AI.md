# index/AI — per-file index (Windows tree)

Generated 2026-07-11. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/AI/TradingTerminal.Ai/Analyst/AiAnalystEnricher.cs` | 111 | win | TradingTerminal.Ai | B I P | Y | Notification enricher that appends a one-line AI Analyst verdict to every signal |
| `src/windows/AI/TradingTerminal.Ai/Analyst/AiAnalystServiceCollectionExtensions.cs` | 63 | win | TradingTerminal.Ai | B I P | Y | Registers the AI Analyst seam. The single registered |
| `src/windows/AI/TradingTerminal.Ai/Analyst/HttpAiAnalystClient.cs` | 156 | win | TradingTerminal.Ai | B I P | Y | HTTP client for the Python daxalgo-ml sidecar's /analyst/run endpoint. |
| `src/windows/AI/TradingTerminal.Ai/Analyst/NullAiAnalystClient.cs` | 17 | win | TradingTerminal.Ai | B I P | Y | Stand-in registered when AiAnalystOptions.Enabled is false (no Python sidecar |
