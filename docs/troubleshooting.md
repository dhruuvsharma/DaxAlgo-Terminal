# Troubleshooting

> Last updated: 2026-05-25

Consolidated symptom → likely cause / fix table across every subsystem. For deeper context, follow the cross-links.

## Build / SDK

| Symptom | Cause / fix |
|---|---|
| `dotnet build` complains about missing .NET 8 SDK | This project targets `net9.0-windows`. Install the .NET 9 SDK. |
| Tests fail with a STA error | The `Xunit.StaFact` package didn't restore. The WPF-touching test uses `[WpfFact]` which spins up an STA thread. Run `dotnet restore` again. |
| Build error after pulling: `_wpftmp.csproj` can't see a type | WPF `MarkupCompilePass1` limitation. Clean `obj/` and rebuild, OR move the offending type to a referenced assembly. |
| `IB CSharpAPI resolved from:` is missing from the build output | `CSharpAPI.dll` couldn't be found. See [brokers.md](brokers.md#dll-resolution-order). The synthetic `FakeIbClient` will run instead. |
| `NTDirect resolved from:` is missing | `NTDirect.dll` couldn't be found at any of the resolution paths. See [brokers.md](brokers.md#dll-resolution-order). |

## Interactive Brokers

| Symptom | Cause / fix |
|---|---|
| Login banner permanently red | TWS isn't running, or its socket port doesn't match `appsettings.json`. Default TWS Paper is 7497. |
| `IB error 502: Couldn't connect to TWS` | API mode not enabled in TWS. Enable **API → Settings → Enable ActiveX and Socket Clients** and add `127.0.0.1` to trusted IPs. |
| `IB error 326: client id is already in use` | Change `InteractiveBrokers:ClientId` to a value not used by any other client (Excel, Bookmap, another instance of the terminal). |
| `IB error 10089: requires additional subscription` | No real-time market-data sub on that contract. Switch `MarketDataType` to `3` (Delayed) in the login form. |
| Chart shows synthetic random-walk bars | `RealIbClient` wasn't compiled in (no `IB CSharpAPI resolved from:` line at build time). Drop `CSharpAPI.dll` into `lib/` or install TWS API to its standard path. |

## NinjaTrader 8

| Symptom | Cause / fix |
|---|---|
| `rc != 0` on connect | NT 8 isn't running, or **Tools → Options → AT Interface → AT Interface enabled** isn't ticked, or `UseRealClient` is false. |
| `DllNotFoundException` on connect | `NTDirect.dll` wasn't copied next to the assembly. Verify the build printed `NTDirect resolved from:`. |
| Charts show flat lines / no bars | NT has no historical bar API; the client synthesizes a series anchored on the current `LastPrice`. Expected — get a real broker if you need real history. |
| `Tick.BidSize` / `Tick.AskSize` always 0 | NTDirect doesn't expose L1 sizes. Expected — there is no workaround inside the AT Interface. |

## cTrader

| Symptom | Cause / fix |
|---|---|
| Connect fails immediately | One of `ClientId` / `ClientSecret` / `AccessToken` / `CtidTraderAccountId` is missing or wrong. Check the Logs pane for the exact `ProtoOAErrorRes` description. |
| Was working, now fails | Access token expired (~30 days). Re-run the OAuth refresh and paste the new token into the login form. |
| Depth events never fire | The symbol may not have L2 enabled in your broker's account. Try a major FX pair or a CFD with known depth. Check Logs for `ProtoOASubscribeDepthQuotesReq` errors. |

## Alpaca

| Symptom | Cause / fix |
|---|---|
| Login fails immediately (`auth failed`) | API key id or secret is wrong, or the key was minted on a different environment (paper key against the live endpoint or vice versa). Re-check the paper / live toggle; regenerate the key from the dashboard if needed. |
| `NotSupportedException` on subscribe | The contract's `SecType` isn't `STK` or `CRYPTO`. Options aren't wired yet — route options through IB. |
| `NotSupportedException` from `SubscribeDepthAsync` | Expected — Alpaca only exposes L1 quotes. Route depth-of-market subscriptions through cTrader. |
| `sip` stock feed returns no data | The `sip` consolidated feed needs a paid Alpaca market-data subscription. Switch the feed dropdown to `iex` (free). |

## Market-data store

| Symptom | Cause / fix |
|---|---|
| Log: `Postgres unreachable — falling back to embedded SQLite store.` | `MarketDataStore:Provider=Postgres` but Docker isn't running (or the connection string is wrong). Start the service with `docker compose up -d`, or set `Provider` to `Sqlite` if you don't need Postgres. The app keeps running on SQLite either way. |
| Postgres logs `password authentication failed` | The default `docker-compose.yml` credentials are `daxalgo / daxalgo`. If you changed them, update `MarketDataStore:PostgresConnectionString` to match — or `docker compose down -v` to wipe the volume and re-init with the new env vars. |
| Store grows without bound | Configure the Telegram archive offloader at **Settings → Market data archive**. See [market-data.md](market-data.md#market-data-archive-telegram-offloader). |

## Notifications

| Symptom | Cause / fix |
|---|---|
| Notifications not arriving | Open the Logs pane — Telegram / Discord transports log failures there. Common: invalid bot token (Telegram), expired or malformed webhook URL (Discord). Hit **Send test** in the Settings tab to bypass strategy logic. |
| Discord webhook returns 401 | Webhook was deleted on the Discord side. Recreate via *Edit Channel → Integrations → Webhooks*. |
| Ollama enricher silently doing nothing | The model isn't pulled, or Ollama isn't running. `ollama list` to confirm; `ollama serve` to start. The enricher always times out silently — deliberate, so a slow LLM never backlogs the dispatcher. |

## AI Market Analyst

| Symptom | Cause / fix |
|---|---|
| WPF pane says "AI Analyst unavailable" | The sidecar isn't running or the port in Settings doesn't match. Hit `http://127.0.0.1:<port>/healthz` directly to verify. |
| HTTP 504 — analyst run timed out | The 60-second top-level timeout tripped. The first vision call is often slow; retry once. |
| HTTP 500 — API key for provider 'openai' is empty | Populate the API key under Settings → Notifications → AI Analyst before clicking Analyze. |
| Wrong-paste API key | The field is DPAPI-encrypted; a wrong paste won't round-trip cleanly. Re-paste and Save. |
| TA-Lib install fails on Windows | Grab a prebuilt wheel from <https://www.lfd.uci.edu/~gohlke/pythonlibs/#ta-lib> matching your Python version. |

## Market regime

| Symptom | Cause / fix |
|---|---|
| Panel stays on "unavailable" | First refresh hasn't landed yet (give it `RefreshMinutes`), or every source failed. Check Logs for per-source warnings. Without a `FredApiKey`, credit / liquidity / macro fall back to neutral but the Yahoo-driven categories still compute. |
| Signal notifications stopped, dashboard still shows them | `MarketRegime:GateSignalsWhenRiskOff` is on and the composite dropped below `RiskOffThreshold`. Lower the threshold, untick the gate, or wait for risk-on to return — the suppression reason is logged for each dropped notification. |
| `RegimeChange` alerts spam the channel | The composite is oscillating near a band boundary. Increase `RefreshMinutes` to smooth, or set `NotifyOnRegimeChange: false`. |

## Strategies / backtest

| Symptom | Cause / fix |
|---|---|
| Strategy window shows `AvalonDock.Layout.LayoutDocument` text | Stale build before the DockTab fix. `dotnet build` again. |
| Strategy doesn't fire signals on synth data | Many strategies are regime-specific (session-aware, gap-aware, sticky-touch, etc.). Synth random-walk doesn't reproduce those regimes. Use real recorded data via the Recorder tab. |
| Backtest CLI: `Unknown strategy 'foo'` | Run with `--strategy` set to one of the canonical IDs. Run `daxalgo-backtest` with no args to list them. |
| Backtest equity curve flat after first trade | Most strategies need a warm-up window before they fire. Check `summary.json` for `TickCount`; if the run was too short, none of the indicators are armed yet. |

## Where to escalate

If a symptom isn't here, the Logs pane is the next place to look — every subsystem logs to it via the in-memory Serilog sink. The file sink at `logs/terminal-YYYY-MM-DD.log` has the same content if you've already closed the app.
