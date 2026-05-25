# AI Market Analyst

> Last updated: 2026-05-25

A multi-agent LangGraph analyst that runs an indicator → pattern → trend → decision flow on a chosen symbol/timeframe and returns a structured verdict (`Long` / `Short` / `NoCall`) with annotated candlestick + trend-channel charts. The reasoning lives in a Python sidecar (`tools/python-ml/daxalgo-ml.exe`) — the WPF build stays hermetic. Bring your own API key for OpenAI, Anthropic, Qwen, or MiniMax.

For the polyglot architecture rationale, see [polyglot.md](polyglot.md).

## What it does

`POST /analyst/run` takes a window of OHLCV bars + a provider/model selection and runs a four-agent flow:

1. **Indicator agent** — computes RSI / MACD / ATR / EMA via TA-Lib, asks the text LLM to summarise the regime.
2. **Pattern agent** — renders a candlestick PNG, asks the vision LLM to match it against a 16-pattern classical catalog (inverse H&S, double bottom, wedges, flags, triangles, V-reversal, etc.).
3. **Trend agent** — fits an upper/lower linear channel on highs/lows with one round of 2-sigma outlier rejection, renders the annotated chart, asks the vision LLM for the trend regime.
4. **Decision agent** — synthesises the three reports into a strict-JSON verdict (`long` / `short` / `no_call`), with up to 3 retries when the LLM returns invalid JSON.

The response is the full structured `AnalystReport` including base64-encoded PNG charts that the WPF view binds straight into `Image` controls.

## Setup

### 1. Build the sidecar

From `tools/python-ml/`:

```powershell
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -e .[dev]
pyinstaller daxalgo_ml.spec
```

That produces `dist/daxalgo-ml.exe`. Copy to `tools/python-ml/bin/daxalgo-ml.exe`.

TA-Lib is a C-extension with no PyPI wheel for some Python versions on Windows. If `pip install TA-Lib` fails, grab a prebuilt wheel from <https://www.lfd.uci.edu/~gohlke/pythonlibs/#ta-lib> matching your Python version and install it manually. The indicator agent has a NumPy-only fallback path so tests don't require TA-Lib.

### 2. Run the sidecar

```powershell
$env:DAXALGO_ML_PORT = "8765"
.\bin\daxalgo-ml.exe
```

The service binds to `127.0.0.1` only — never to `0.0.0.0`. Loopback-only + process-bound lifetime = same trust boundary as the WPF app itself.

### 3. Wire it up in the terminal

**Tools → Settings → Notifications**, scroll to the **AI Market Analyst** block:

- Tick **Enabled**.
- Set the endpoint to `http://127.0.0.1:8765`.
- Pick your provider (OpenAI / Anthropic / Qwen / MiniMax).
- Paste the API key.
- Set the text and vision model IDs.
- **Save**.

The API key is stored DPAPI-encrypted under `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` (scope: current user, current machine). It is never written in plain text and never appears in `appsettings.json`.

## Running an analysis

**AI tools → Market analyst** opens the dock pane. Type a symbol, pick a timeframe and bar count, hit **Analyze**. The terminal pulls bars from the active broker, ships them to the sidecar, and renders the verdict:

- **Left column** — indicator commentary (RSI / MACD / ATR / EMA panel + plain-English summary).
- **Middle column** — pattern verdict (one of 16 classical patterns) plus the rendered candlestick PNG the vision LLM scored against.
- **Right column** — trend regime (Up / Down / Flat) plus an annotated chart showing the fitted upper/lower linear channel.
- **Bottom strip** — big LONG / SHORT / NO-CALL badge, R:R, forecast horizon, justification, confidence.

Every Analyze click runs fresh — there is no cache. The terminal is signal-mode by rule: the analyst proposes, the human disposes. There is no "place this trade" button on the pane.

## Per-notification enrichment

Tick **Append the AI Analyst verdict line to every signal notification** in Settings, and each Telegram / Discord signal carries an extra line like:

```
AI Analyst: Long (conf 72%, R:R 2.10) — Bull Flag
```

The enricher runs independently of Ollama; both can be on at once. If the sidecar is down or the call times out, the original notification goes out unchanged — the AI line is strictly additive.

## Graceful degradation

With no sidecar running (or **Enabled** off), the AI Analyst pane renders the empty state cleanly: "AI Analyst unavailable" badge, Analyze button disabled. Nothing else in the app changes — the existing Ollama enricher and the standard notification pipeline keep working.

The C# side picks between `NullAiAnalystClient` (default — always returns "unavailable") and `HttpAiAnalystClient` (calls the sidecar). A `DispatchingAiAnalystClient` reads `IOptionsMonitor<NotificationsOptions>.CurrentValue` on every call so the Settings toggle hot-swaps Null ↔ Http without a restart.

## Troubleshooting

- **"AI Analyst unavailable" in the WPF pane.** Hit `http://127.0.0.1:<port>/healthz` directly in a browser. If it doesn't return `{"status": "ok"}`, the sidecar isn't running or the port in Settings is wrong.
- **HTTP 504 — analyst run timed out.** The 60-second top-level timeout tripped — the first vision call is often slow; retry once.
- **HTTP 500 — API key for provider 'openai' is empty.** Populate the API key under Settings → Notifications → AI Analyst before clicking Analyze.
- **Wrong-paste API key.** The field is stored DPAPI-encrypted, so a wrong paste won't ever round-trip cleanly; re-paste and Save.
- **Common sidecar console errors.** Missing API key, invalid model id, missing TA-Lib install (use the Gohlke wheel on Windows), vision LLM rejected the image.

## Architecture summary

```csharp
public interface IAiAnalystClient
{
    bool IsAvailable { get; }
    Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default);
}

public sealed record AnalystReport(
    AiAnalystDecision Decision, string ForecastHorizon, double RiskRewardRatio,
    double Confidence, string Justification,
    IndicatorReport Indicator, PatternReport Pattern, TrendReport Trend,
    string PatternChartPngBase64, string TrendChartPngBase64, long ElapsedMs);
```

Two implementations:

- `NullAiAnalystClient` (default — always returns "unavailable").
- `HttpAiAnalystClient` — calls the Python sidecar over `http://127.0.0.1:<port>/analyst/run`.

A `DispatchingAiAnalystClient` reads `IOptionsMonitor<NotificationsOptions>.CurrentValue` on every call so the Settings toggle hot-swaps Null ↔ Http without a restart.

See `tools/python-ml/` for the sidecar source and [polyglot.md](polyglot.md) for the subprocess + HTTP/JSON seam contract.
