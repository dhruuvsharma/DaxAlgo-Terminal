# TradingTerminal.Infrastructure — brokers, engine, plugins, research

**Path** `src/windows/Pipeline/TradingTerminal.Infrastructure/` · 14,855 LOC / 103 files · **Editions** B I P · **Blast: HIGH**

**Purpose.** All concrete integrations: 12 broker clients + Simulated, backtest engine internals,
notifications, regime services, plugin loader, research sandbox, `WpfDispatcher`.

**Layout / symbols.** Per-broker folders (`Ib/ Ninja/ CTrader/ Alpaca/ IronBeam/ LondonStrategicEdge/
Upstox/ Binance/ Coinbase/ Bybit/ Kraken/ Okx/ Simulation/`) → `symbols/Infrastructure-<Broker>.md`;
`Backtest/` (fills, fees, risk, session, catalog) → `Infrastructure-Backtest.md`; `Plugins/`
(PluginLoader ALC, manifest, Authenticode trust, **add-only `GuardedServiceCollection` registrar
guard** — a plugin may not replace a host service) → `Infrastructure-Plugins.md`; `Research/`
(Docker sandbox — load `untrusted-execution` BEFORE touching) → `Infrastructure-Research.md`;
`Notifications/`, `Regime/`. DI root: `DependencyInjection.cs` (`AddCredentialedBrokers`:100,
`AddKeylessBrokers`:233) → `Infrastructure-Root.md`.

**Depends on** Core, MarketData, DaxAlgo.Sdk. **Depended by** all tools/charts, Ai, Settings, both shells, tests.

**Invariants.** SDK types never leak upward (hook-enforced); broker callbacks never touch the UI
thread (repository marshals); `ConnectAsync()` reads own options; reconnect backoff 1s→30s;
IB/NT wired purely by build-time DLL resolution (`HAS_IBAPI`/`HAS_NTAPI`).

**Tests** Tests.Headless `~<Broker>` / `~Backtest` / `~Research`. **Common changes.**
`RECIPES/add-broker.md`; per-broker quirks → `broker-gotchas` skill; IB → `ib-api-expert` agent.
