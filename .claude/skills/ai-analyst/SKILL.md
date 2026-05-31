---
name: ai-analyst
description: Python sidecar (LangGraph indicator/pattern/trend/decision agents + TA-Lib + vision LLM) reached over HTTP/JSON via IAiAnalystClient; hot-swappable Null↔Http via IOptionsMonitor. Use when touching tools/python-ml/, the IAiAnalystClient seam in Core/Infrastructure, the AiAnalystEnricher in the notification pipeline, the AI Market Analyst dock pane, or debugging "AI Analyst unavailable" / HTTP 504 / 500 errors.
---

# AI Analyst

A multi-agent LLM analyst lives in a Python sidecar so the C# build stays hermetic — no Python, no TA-Lib C extension in the .NET tree. The WPF app talks to it over `http://127.0.0.1:<port>/analyst/run`. Bring your own API key (OpenAI / Anthropic / Qwen / MiniMax).

## Architecture

C# side (`src/TradingTerminal.Core/AiAnalyst/` + `src/TradingTerminal.Ai/Analyst/`):

- **`IAiAnalystClient`** — `bool IsAvailable` + `Task<AnalystReport> RunAsync(AnalystRequest, ct)`.
- **`NullAiAnalystClient`** — always returns "unavailable". Default when sidecar isn't enabled.
- **`HttpAiAnalystClient`** — POSTs to the sidecar. Uses named `HttpClient` (`HttpAiAnalystClient.HttpClientName`) with a 2 min outer ceiling; per-call timeout via `AiAnalystOptions.TimeoutSeconds` (default 60s).
- **`DispatchingAiAnalystClient`** — the registered `IAiAnalystClient`. Reads `IOptionsMonitor<NotificationsOptions>.CurrentValue.AiAnalyst.Enabled` on every call → hot-swaps Null↔Http without restart.
- **`AiAnalystEnricher : INotificationEnricher`** — appends `AI Analyst: Long (conf 72%, R:R 2.10) — Bull Flag` to every Telegram/Discord signal when enabled. Failures swallowed silently → original notification still ships.

Python side (`tools/python-ml/`):

- `daxalgo_ml/` package, `pyproject.toml`, `daxalgo_ml.spec` (PyInstaller).
- Build: `pip install -e .[dev]` then `pyinstaller daxalgo_ml.spec` → `dist/daxalgo-ml.exe`. Drop into `tools/python-ml/bin/`.
- Run: `$env:DAXALGO_ML_PORT = "8765"; .\bin\daxalgo-ml.exe`.
- **Binds `127.0.0.1` only.** Never `0.0.0.0`.
- Four-agent LangGraph:
  1. **Indicator** — TA-Lib RSI / MACD / ATR / EMA + text LLM regime summary. NumPy-only fallback if TA-Lib is missing (so tests don't require it).
  2. **Pattern** — matplotlib candlestick PNG → vision LLM scored against a 16-pattern classical catalog.
  3. **Trend** — fit upper/lower linear channel on highs/lows with one round of 2-sigma outlier rejection → annotated PNG → vision LLM for trend regime.
  4. **Decision** — strict-JSON verdict (`long`/`short`/`no_call`), up to 3 retries on invalid JSON.

Response: full `AnalystReport` with base64-encoded PNG charts the WPF view binds directly to `Image` controls.

## Settings + secret handling

Configured under **Tools → Settings → Notifications → AI Market Analyst**:

- Enabled toggle.
- Endpoint (`http://127.0.0.1:8765` default).
- Provider (OpenAI / Anthropic / Qwen / MiniMax).
- API key — **DPAPI-encrypted in `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`**. Never plain-text. Never in `appsettings.json`. Scope: current user, current machine.
- Text model id + vision model id.
- Per-notification enrichment toggle (separate from Ollama; both can run).

## When something breaks

- **"AI Analyst unavailable" in the pane.** Hit `http://127.0.0.1:<port>/healthz` in a browser. Not returning `{"status":"ok"}` → sidecar not running OR wrong port in Settings.
- **HTTP 504 — analyst run timed out.** First vision call is often slow. Retry once. If chronic, raise `AiAnalystOptions.TimeoutSeconds` (don't exceed the outer 2 min ceiling).
- **HTTP 500 — API key for provider 'X' is empty.** User skipped Save after pasting. Re-paste + Save.
- **Wrong-paste API key.** DPAPI round-trip will fail → re-paste + Save.
- **Sidecar console: missing TA-Lib.** Use the Gohlke prebuilt wheel for Windows (`pip install TA-Lib` fails for some Python versions); or rely on the NumPy fallback for unit tests.
- **`AiAnalystEnricher` silently dropped a line.** That's by design — sidecar failure must not break the notification path. Check Serilog for the failure trace.

## Hard rules

- **Sidecar binds `127.0.0.1` only.** Loopback + process-bound lifetime = same trust boundary as the WPF process. Binding to `0.0.0.0` would expose API keys + arbitrary chart rendering to anything on the LAN.
- **No Python or TA-Lib in the C# build.** The seam stays HTTP/JSON subprocess. If you find yourself adding `<PackageReference Include="Python.Net" />` — stop.
- **No "Place this trade" button on the analyst pane.** Signal-mode only by rule. The analyst proposes; the human disposes.
- **Enricher must never throw or block.** Sidecar down = original notification unchanged, no exception surfaces.
- **Hot-swap via `IOptionsMonitor.CurrentValue`** — read fresh on every call. Don't cache the `Enabled` flag.

## Reference reads

- `src/TradingTerminal.Core/AiAnalyst/IAiAnalystClient.cs` — seam.
- `src/TradingTerminal.Ai/Analyst/AiAnalystServiceCollectionExtensions.cs` — DI.
- `src/TradingTerminal.Ai/Analyst/HttpAiAnalystClient.cs` — HTTP path.
- `src/TradingTerminal.Ai/Analyst/AiAnalystEnricher.cs` — enricher.
- `tools/python-ml/daxalgo_ml/` — sidecar source.
- `docs/ai-analyst.md` + `docs/polyglot.md` — user-facing + architecture prose.

See also: [add-notifier](../add-notifier/SKILL.md), [[project-ai-analyst]] (memory).
