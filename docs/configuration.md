# Configuration reference

> Last updated: 2026-06-08

Full reference for every key under `appsettings.json` plus the persistence locations for runtime state. For per-broker setup, see [brokers.md](brokers.md). For feature deep-dives that explain *why* a setting exists, follow the cross-links.

## File overlay order

The terminal reads configuration in this order (later layers win):

1. `appsettings.json` at the app's working directory — checked into the repo, defaults only.
2. `appsettings.{Environment}.json` — selected by `DOTNET_ENVIRONMENT` (the dev launch profiles set this to `DevSim` / `DevReplay` / `DevLive`). Off in the shipped build. See [Dev launch profiles](#dev-launch-profiles).
3. `appsettings.local.json` next to it — gitignored, for per-machine overrides (wins over the dev env file).
4. `%LOCALAPPDATA%\DaxAlgo Terminal\notifications.json` — written by the Notifications settings tab; `reloadOnChange: true`.
5. Environment variables.

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
| `MarketDataType` | `1` | `1` Live / `3` Delayed / `4` Delayed-Frozen. |
| `ReconnectInitialDelaySeconds` | `1` | Initial exponential backoff. |
| `ReconnectMaxDelaySeconds` | `30` | Cap on backoff. |

### `NinjaTrader`

| Key | Default | Notes |
|---|---|---|
| `AccountName` | `Sim101` | NT account to drive (default sim). |
| `DefaultFuturesContractMonth` | (empty) | Appended to bare futures symbols, e.g. `ES 06-26`. |

> **Note:** IB and NT are wired purely by DLL resolution at build time (`HAS_IBAPI` / `HAS_NTAPI`) — there's no longer a `UseRealClient` switch for either. The key still present in `appsettings.json` is vestigial and ignored. To run with no broker, use the [`Simulated`](#simulatedbroker) backend.

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

## Dev launch profiles

`src/TradingTerminal.App/Properties/launchSettings.json` defines launch profiles that set `DOTNET_ENVIRONMENT`, which layers a matching `appsettings.{Env}.json` (repo root) over `appsettings.json`. These are developer conveniences — **off in the shipped build** (no `DOTNET_ENVIRONMENT` set ⇒ no dev file loaded).

| Profile | `DOTNET_ENVIRONMENT` | Dev file | Behaviour |
|---|---|---|---|
| `App (Login)` | *(none)* | — | Normal — login window shown. |
| `Dev: Simulated (offline)` | `DevSim` | `appsettings.DevSim.json` | Skips login; `Simulated` broker, **Synthetic** feed. Pins `Sqlite` + `PersistLiveData:false`. Fully offline. |
| `Dev: Replay (local DB)` | `DevReplay` | `appsettings.DevReplay.json` | Skips login; `Simulated` broker, **Replay** of the local store (10× clock). `PersistLiveData:false` so replayed data isn't re-written. |
| `Dev: Live (no login)` | `DevLive` | `appsettings.DevLive.json` | Skips login; auto-connects a real broker (default IB) using saved credentials. |

### `Dev`

Bound from the `Dev` section (`DevOptions`). Off by default in the shipped `appsettings.json`.

| Key | Default | Notes |
|---|---|---|
| `Dev:BypassLogin` | `false` | When true, the app skips the login window, auto-connects `AutoConnectBrokers`, and opens the main shell directly. |
| `Dev:AutoConnectBrokers` | `[]` | Brokers to connect when `BypassLogin` is set. Started through the same `IBrokerSelector.ConnectAsync` the login forms use; a connect that fails (e.g. no saved credentials) is logged to the Activity Log and skipped — never fatal. |

### `SimulatedBroker`

Bound from the `SimulatedBroker` section (`SimulatedBrokerOptions`). Drives the in-process `Simulated` broker (`BrokerKind.Simulated`) — see [brokers.md](brokers.md#simulated-offline-development).

| Key | Default | Notes |
|---|---|---|
| `SimulatedBroker:Mode` | `Synthetic` | `Synthetic` (in-process random walk) or `Replay` (re-emit recorded store data). |
| `SimulatedBroker:SpeedMultiplier` | `1.0` | Wall-clock scaling for replay (`60` = a minute per second); also compresses the synthetic cadence. |
| `SimulatedBroker:Loop` | `true` | **Replay.** Restart from the beginning of the window once stored data is exhausted. |
| `SimulatedBroker:MaxGapSeconds` | `2` | **Replay.** Cap (seconds, pre-scaling) on the idle wait between two stored events, so overnight gaps don't stall replay. |
| `SimulatedBroker:ReplayLookbackDays` | `30` | **Replay.** Window: the last N days of stored data, ending now. |
| `SimulatedBroker:SyntheticBarSize` | `OneMinute` | **Synthetic.** Bar size emitted by the synthetic bar stream. |
| `SimulatedBroker:SyntheticTickIntervalMs` | `250` | **Synthetic.** Interval between quote/tick/trade emissions. |
| `SimulatedBroker:SyntheticBarIntervalMs` | `2000` | **Synthetic.** Interval between bar emissions. |
| `SimulatedBroker:SyntheticStartPrice` | `100.0` | **Synthetic.** Random-walk starting price. |
| `SimulatedBroker:SyntheticVolatility` | `0.0015` | **Synthetic.** Per-step stdev as a fraction of price. |
| `SimulatedBroker:Seed` | `1234` | **Synthetic.** Seed, so reruns are reproducible. |
| `SimulatedBroker:Instruments` | `["AAPL","MSFT","ES","NQ","BTCUSD"]` | **Synthetic.** Symbols surfaced by `ListInstrumentsAsync`. (Replay lists whatever the store holds.) |

## Market data store

| Key | Default | Notes |
|---|---|---|
| `MarketDataStore:Enabled` | `true` | Master switch for the persistence + ingest pipeline. The in-memory hub still works when this is false; only the disk writes stop. |
| `MarketDataStore:Provider` | `QuestDb` | `Sqlite` (embedded, zero-config), `Postgres` (PostgreSQL + TimescaleDB), or `QuestDb` (L1/L2 → QuestDB, bars → SQLite). The shipped `appsettings.json` defaults to `QuestDb`; the dev `DevSim` profile pins `Sqlite` for a no-Docker offline run. Postgres falls back to SQLite at startup if unreachable; QuestDb does **not** (tick/depth persistence is disabled instead). |
| `MarketDataStore:PostgresConnectionString` | `Host=localhost;Port=5432;Database=daxalgo;Username=daxalgo;Password=daxalgo;Timeout=5;Command Timeout=10` | Matches the `docker-compose.yml` service. Only used when `Provider=Postgres`. |
| `MarketDataStore:DatabasePath` | (empty) | SQLite file path. Empty = `%LOCALAPPDATA%\DaxAlgoTerminal\marketdata.db`. |
| `MarketDataStore:PersistLiveData` | `true` | When false the hub still fans out in-memory but nothing is written to disk. |
| `MarketDataStore:WriteBatchSize` | `500` | Records buffered before the background writer flushes. |
| `MarketDataStore:FlushIntervalMs` | `1000` | Max wait before a flush even if the batch isn't full. |
| `MarketDataStore:QuoteRetentionDays` | `30` | **Postgres/Timescale only.** Installs a TimescaleDB retention job on `quotes`. `0` = keep forever. No effect on SQLite. |
| `MarketDataStore:TradeRetentionDays` | `30` | Same, for `trades`. |
| `MarketDataStore:BarRetentionDays` | `0` | Same, for `bars`. `0` = keep forever (bars are tiny). |
| `MarketDataStore:QuestDbIlpConfig` | `http::addr=localhost:9000;auto_flush=off;` | **QuestDb only.** ILP client config (HTTP transport) for quote/trade/depth ingest. `auto_flush=off` so the batched writer flushes one `Send()` per batch. |
| `MarketDataStore:QuestDbPgConnectionString` | `Host=localhost;Port=8812;Database=qdb;Username=admin;Password=quest;...;ServerCompatibilityMode=NoTypeLoading` | **QuestDb only.** PG-wire endpoint for schema creation and replay reads. |
| `MarketDataStore:DepthRetentionDays` | `14` | **QuestDb only.** Partition TTL on the `depth` table (best-effort; needs a QuestDB build supporting `SET TTL`). `0` = keep forever. |

See [market-data.md](market-data.md) for the pipeline architecture and operational notes, and [storage.md](storage.md) for the map of every storage surface.

## Parquet lake

Opt-in local Parquet mirror of the store's closed periods, laid out for direct DuckDB querying. Off by default; independent of the Telegram archive. See [storage.md](storage.md).

| Key | Default | Notes |
|---|---|---|
| `MarketDataParquetLake:Enabled` | `false` | Master switch. When false the export service idles (one cheap timer tick / 15 min). |
| `MarketDataParquetLake:RootDirectory` | (empty) | Lake root. Empty = `%LOCALAPPDATA%\DaxAlgo Terminal\parquet-lake`. |
| `MarketDataParquetLake:Period` | `Monthly` | `Monthly` or `Weekly` — how often a period closes and gets exported. |
| `MarketDataParquetLake:Tables` | `Quotes, Bars, Trades` | Which tables to export (comma-separated flags). |
| `MarketDataParquetLake:DailyCheckHourUtc` | `4` | UTC hour to check for a closed period. Offset from the archive's hour so they don't hit the store at once. |

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
| `%LOCALAPPDATA%\DaxAlgoTerminal\archive-manifest.db` | Archive offloader's manifest (what's been shipped to Telegram, with sha256s). Override via `MarketDataArchive:ManifestDatabasePath`. |
| `%LOCALAPPDATA%\DaxAlgo Terminal\recordings\` | Default output folder for the Tools → Record live ticks parquet files. |
| `%LOCALAPPDATA%\DaxAlgo Terminal\parquet-lake\` | Parquet lake export tree (opt-in). Override via `MarketDataParquetLake:RootDirectory`. |
| `./bt-results/` (working dir) | Default output folder for the backtest CLI's `run` subcommand (`summary.json`, `trades.csv`, `equity.csv`, `fills.csv`). |
| `./logs/terminal-YYYY-MM-DD.log` (working dir) | Daily rolling Serilog file output. |
