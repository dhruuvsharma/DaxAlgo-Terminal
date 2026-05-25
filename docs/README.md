# DaxAlgo Terminal — Documentation

> Last updated: 2026-05-25

Focused documentation for the DaxAlgo Terminal. The repo-root [README](../README.md) is a one-page landing; this folder is where the detail lives.

## For new users

| Doc | What it covers |
|---|---|
| [Getting started](getting-started.md) | Prereqs, clone, build, first launch. The 10-minute path from zero to a running shell. |
| [Broker setup](brokers.md) | Per-broker configuration: Interactive Brokers, NinjaTrader 8, cTrader, Alpaca. Credentials, ports, OAuth, common pitfalls. |
| [User guide](user-guide.md) | Daily-use walkthrough — login, running strategies live, recording ticks, factor research, CLI cheat sheet. |
| [Troubleshooting](troubleshooting.md) | Consolidated symptom → fix table across all subsystems. |

## Features in depth

| Doc | What it covers |
|---|---|
| [Strategies](strategies.md) | The 20+ shipped strategies, their parameters, what each is good for. Plus the recipe for adding a new strategy. |
| [Backtesting](backtesting.md) | Tick-level engine, fees, risk caps, CLI (`run` / `sweep` / `walkforward` / `mc` / `tca` / `features`). |
| [Market data pipeline](market-data.md) | Canonical pipeline (hub, ingest, store), SQLite vs Postgres/TimescaleDB backends, the Telegram archive offloader. |
| [Market regime](market-regime.md) | The 0–100 risk-on / risk-off composite — sources, weights, optional signal gate. |
| [Notifications](notifications.md) | Telegram and Discord transports, Ollama commentary enricher, adding a new transport. |
| [AI Market Analyst](ai-analyst.md) | Python sidecar setup, four-agent flow, provider/model selection, per-signal enrichment. |

## Engineering reference

| Doc | What it covers |
|---|---|
| [Architecture](architecture.md) | Design rationale, key interfaces, threading model, project graph. Read this before adding abstractions. |
| [Configuration reference](configuration.md) | Full `appsettings.json` key reference + persistence locations + secret storage. |
| [Polyglot architecture](polyglot.md) | The subprocess + JSON seam for the C++ tick backtester and the Python AI sidecar. |
| [Contributing](contributing.md) | Adding a strategy, broker, or notifier without breaking the layering rules. |

## Conventions used in this documentation

- All timestamps in docs are UTC; instrument bar timestamps are UTC unless explicitly stated.
- All paths assume Windows (`%LOCALAPPDATA%\…`); the project is `net9.0-windows` and not cross-platform.
- "Live mode" means the real broker SDK is wired; "synthetic / fake mode" means the `Fake*Client` random-walk fallback (available for IB, NT, cTrader — not Alpaca).
- Code references use `file:line` so they remain valid when read in any editor.
