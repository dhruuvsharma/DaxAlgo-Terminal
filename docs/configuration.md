# Configuration reference

> Last updated: 2026-05-25

Full reference for every key under `appsettings.json` plus the persistence locations for runtime state. For per-broker setup, see [brokers.md](brokers.md). For feature deep-dives that explain *why* a setting exists, follow the cross-links.

## File overlay order

The terminal reads configuration in this order (later layers win):

1. `appsettings.json` at the app's working directory — checked into the repo, defaults only.
2. `appsettings.local.json` next to it — gitignored, for per-machine overrides.
3. `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` — written by the Notifications settings tab; `reloadOnChange: true`.
4. Environment variables.

Secrets live in a separate DPAPI-encrypted file:

- `%LOCALAPPDATA%\DaxAlgoTerminal\connection.json` — broker passwords, OAuth tokens, Alpaca API secrets.
- `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` — AI Analyst provider API keys (DPAPI-encrypted at the field level; the rest of the file is plain text).

Note the spelling difference: `DaxAlgoTerminal` (broker creds) and `DaxAlgo Terminal` (notifications). Both folders are created automatically.

## Brokers

### `InteractiveBrokers`

| Key | Default | Notes |
|---|---|---|
| `Host` | `127.0.0.1` | TWS is local. |
| `Port` | `7497` | TWS Paper. Use `7496` (TWS Live), `4002` (Gateway Paper), `4001` (Gateway Live). |
| `ClientId` | `1` | Must be unique across all clients connected to the same TWS. |
| `AccountType` | `Paper` | Cosmetic. |
| `UseRealClient` | `true` | Set to `false` to force `FakeIbClient` even when `CSharpAPI.dll` is present. |
| `MarketDataType` | `1` | `1` Live / `3` Delayed / `4` Delayed-Frozen. |
| `ReconnectInitialDelaySeconds` | `1` | Initial exponential backoff. |
| `ReconnectMaxDelaySeconds` | `30` | Cap on backoff. |

### `NinjaTrader`

| Key | Default | Notes |
|---|---|---|
| `AccountName` | `Sim101` | NT account to drive (default sim). |
| `DefaultFuturesContractMonth` | (empty) | Appended to bare futures symbols, e.g. `ES 06-26`. |
| `UseRealClient` | `false` | Set to `true` after dropping `NTDirect.dll` into a resolvable path. |

### `CTrader`

| Key | Default | Notes |
|---|---|---|
| `Host` | `demo.ctraderapi.com` | Or `live.ctraderapi.com`. |
| `Port` | `5035` | TLS port for both endpoints. |
| `IsLive` | `false` | Cosmetic — the host string is what actually routes. |

OAuth credentials are entered on the login form, never in `appsettings.json`.

### `Alpaca`

| Key | Default | Notes |
|---|---|---|
| `ApiKey` | (empty) | Key id from the Alpaca dashboard (`PK…` paper / `AK…` live). |
| `ApiSecret` | (empty) | API secret. Stored DPAPI-encrypted at runtime — the login form is the normal entry path. |
| `IsLive` | `false` | `true` targets `api.alpaca.markets`; `false` targets `paper-api.alpaca.markets`. |
| `StockDataFeed` | `iex` | `iex` (free) or `sip` (paid). Ignored for crypto. |
| `ReconnectInitialDelaySeconds` | `1` | Initial backoff. |
| `ReconnectMaxDelaySeconds` | `30` | Cap on backoff. |

## Market data store

| Key | Default | Notes |
|---|---|---|
| `MarketDataStore:Enabled` | `true` | Master switch for the persistence + ingest pipeline. The in-memory hub still works when this is false; only the disk writes stop. |
| `MarketDataStore:Provider` | `Postgres` | `Sqlite` (embedded) or `Postgres` (PostgreSQL + TimescaleDB). Postgres falls back to SQLite at startup if the DB is unreachable. |
| `MarketDataStore:PostgresConnectionString` | `Host=localhost;Port=5432;Database=daxalgo;Username=daxalgo;Password=daxalgo;Timeout=5;Command Timeout=10` | Matches the `docker-compose.yml` service. Only used when `Provider=Postgres`. |
| `MarketDataStore:DatabasePath` | (empty) | SQLite file path. Empty = `%LOCALAPPDATA%\DaxAlgoTerminal\marketdata.db`. |
| `MarketDataStore:PersistLiveData` | `true` | When false the hub still fans out in-memory but nothing is written to disk. |
| `MarketDataStore:WriteBatchSize` | `500` | Records buffered before the background writer flushes. |
| `MarketDataStore:FlushIntervalMs` | `1000` | Max wait before a flush even if the batch isn't full. |

See [market-data.md](market-data.md) for the pipeline architecture and operational notes.

## Market regime composite

| Key | Default | Notes |
|---|---|---|
| `MarketRegime:Enabled` | `true` | Master switch for the composite + refresh loop. |
| `MarketRegime:RefreshMinutes` | `30` | Poll cadence (5-minute floor in the loop). |
| `MarketRegime:FredApiKey` | (empty) | Free key from fred.stlouisfed.org. Without it, credit/liquidity/macro categories degrade to neutral 50. |
| `MarketRegime:UseCnnFearGreed` | `true` | Pull CNN's Fear & Greed dataviz endpoint. Scraped — toggle off if it gets noisy. |
| `MarketRegime:UseAaiiSentiment` | `true` | Pull the AAII weekly bull/bear survey. Scraped HTML, always non-blocking. |
| `MarketRegime:NotifyOnRegimeChange` | `true` | Fire a `NotificationKind.RegimeChange` alert when the band crosses. |
| `MarketRegime:GateSignalsWhenRiskOff` | `false` | When true, outbound `Signal` notifications are suppressed while the composite is at/below `RiskOffThreshold`. |
| `MarketRegime:RiskOffThreshold` | `40` | Composite score at or below which the market counts as risk-off for the gate. |

See [market-regime.md](market-regime.md) for the scoring methodology.

## Notifications

| Key | Default | Notes |
|---|---|---|
| `Notifications:QueueCapacity` | `256` | Bounded channel size; oldest dropped on overflow. |
| `Notifications:Telegram:Enabled` | `false` | Master toggle for the Telegram transport. |
| `Notifications:Telegram:BotToken` | (empty) | Token from `@BotFather`. Edit via the Settings tab — persisted to `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json`. |
| `Notifications:Telegram:ChatId` | (empty) | Numeric chat ID or `@channelname`. |
| `Notifications:Telegram:IncludeIdleSignals` | `false` | When false, drop signals fired below the strategy's armed threshold. |
| `Notifications:Discord:Enabled` | `false` | Master toggle for the Discord transport. |
| `Notifications:Discord:WebhookUrl` | (empty) | Channel webhook URL: *Edit Channel → Integrations → Webhooks → Copy URL*. |
| `Notifications:Discord:Username` | `DaxAlgo Terminal` | Optional username override. Empty = use webhook default. |
| `Notifications:Discord:IncludeIdleSignals` | `false` | Same semantics as the Telegram knob. |

See [notifications.md](notifications.md) for the dispatcher architecture and the recipe for adding a new transport.

## Logging

| Key | Default | Notes |
|---|---|---|
| `Logging:MinimumLevel` | `Information` | `Verbose` / `Debug` / `Information` / `Warning` / `Error`. |
| `Logging:FilePath` | `logs/terminal-.log` | Daily rolling, relative to the app's working directory. The in-memory sink that powers the Logs pane is always wired regardless of this. |

## Persistence locations

| Location | What it holds |
|---|---|
| `%LOCALAPPDATA%\DaxAlgoTerminal\connection.json` | DPAPI-encrypted broker creds: IB password, cTrader OAuth secret + access token, Alpaca API secret. |
| `%LOCALAPPDATA%\DaxAlgoTerminal\marketdata.db` | Default SQLite market-data store. Override via `MarketDataStore:DatabasePath`. |
| `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` | Telegram + Discord settings (plain text); AI Analyst provider API keys (DPAPI-encrypted at the field level). |
| `%LOCALAPPDATA%\DaxAlgo Terminal\recordings\` | Default output folder for the Tools → Record live ticks parquet files. |
| `./bt-results/` (working dir) | Default output folder for the backtest CLI's `run` subcommand (`summary.json`, `trades.csv`, `equity.csv`, `fills.csv`). |
| `./logs/terminal-YYYY-MM-DD.log` (working dir) | Daily rolling Serilog file output. |
