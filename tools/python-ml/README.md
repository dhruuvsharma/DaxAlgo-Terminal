# daxalgo-ml

Local FastAPI sidecar for the DaxAlgo Terminal **AI Market Analyst**. The WPF terminal
ships a hermetic .NET build and does **not** embed Python — instead, this service runs
in its own process on `127.0.0.1` and the terminal talks to it over loopback HTTP/JSON.

## What it does

`POST /analyst/run` takes a window of OHLCV bars + a provider/model selection and runs a
four-agent LangGraph:

1. **Indicator agent** — computes RSI/MACD/ATR/EMA via TA-Lib, asks the text LLM to
   summarise the regime.
2. **Pattern agent** — renders a candlestick PNG, asks the vision LLM to match it
   against a 16-pattern classical catalog (Inverse H&S, double bottom, wedges, flags,
   triangles, V-reversal, etc.).
3. **Trend agent** — fits an upper/lower linear channel on highs/lows with one round
   of 2-sigma outlier rejection, renders the annotated chart, asks the vision LLM for
   the trend regime.
4. **Decision agent** — synthesises the three reports into a strict-JSON verdict
   (`long` / `short` / `no_call`), with up to 3 retries when the LLM returns invalid
   JSON.

The response is the full structured `AnalystReport` including base64-encoded PNG charts
that the WPF view binds straight into `Image` controls.

## Why a sidecar (not Python.NET, not P/Invoke)

The WPF build must stay hermetic — one `dotnet build`, no native toolchain on the path.
Polyglot tools live behind a subprocess + HTTP/JSON seam (see `docs/polyglot.md`). If the
sidecar is missing or unreachable, the C# `NullAiAnalystClient` takes over and the UI
shows "AI Analyst unavailable" — the terminal never crashes.

## Layout

```
tools/python-ml/
├── pyproject.toml
├── daxalgo_ml.spec              ← PyInstaller spec
├── daxalgo_ml/
│   ├── __init__.py
│   ├── app.py                   ← FastAPI app + /healthz + /analyst/run
│   ├── schemas.py               ← Pydantic mirrors of the C# wire types
│   ├── providers.py             ← OpenAI / Anthropic / Qwen / MiniMax factory
│   └── analyst/
│       ├── __init__.py
│       ├── graph.py             ← LangGraph wiring (set_graph)
│       ├── charting.py          ← matplotlib + mplfinance helpers
│       └── agents/
│           ├── indicator.py
│           ├── pattern.py
│           ├── trend.py
│           └── decision.py
├── tests/                       ← pytest
└── bin/daxalgo-ml.exe           ← PyInstaller-frozen launcher (produced by the spec)
```

## Building

### Dev install

```powershell
cd tools\python-ml
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -e .[dev]
```

TA-Lib is a C-extension with no PyPI wheel for some Python versions on Windows. If
`pip install TA-Lib` fails, grab a prebuilt wheel from
<https://www.lfd.uci.edu/~gohlke/pythonlibs/#ta-lib> matching your Python and install it
manually. The indicator agent has a NumPy-only fallback path so tests don't require it.

### Running locally

```powershell
$env:DAXALGO_ML_PORT = "8765"   # or omit for an ephemeral port
python -m daxalgo_ml.app
```

Then point the WPF Settings → Notifications → AI Analyst panel at
`http://127.0.0.1:8765` and toggle Enabled.

### Frozen exe

```powershell
pyinstaller daxalgo_ml.spec
```

Output lands at `dist/daxalgo-ml.exe`. Copy that to `tools/python-ml/bin/daxalgo-ml.exe`
to match what the WPF launcher expects.

## Tests

```powershell
pytest
```

The test suite mocks the LLM so it runs offline. It exercises the schemas, the chart
renderer (size + base64 round-trip), each agent in isolation against a fake LLM, and
the full graph end-to-end with a stubbed provider.

## Troubleshooting

- **`AI Analyst unavailable` in the WPF pane** — the sidecar isn't running or the port
  in Settings doesn't match. Hit `http://127.0.0.1:<port>/healthz` directly to verify.
- **`HTTP 504 — analyst run timed out`** — the 60-second top-level ceiling tripped.
  Usually a vision LLM that's slow on the first call; retry once.
- **`HTTP 500 — API key for provider 'openai' is empty`** — populate the API key under
  Settings → Notifications → AI Analyst before clicking Analyze.
