# TradingTerminal.Core — domain types, seams, options

**Path** `src/windows/Core/TradingTerminal.Core/` · **Editions** B I P · **Blast: high**

**Purpose.** Broker-/UI-free domain layer: every cross-project type, interface, and options record.
**Depends on** nothing (hard invariant). **Depended by** every other project.

**Entry points by folder** (symbols split accordingly): `MarketData/` (IBrokerClient, hub/ingest/store
seams, Quote/TradePrint/OhlcvBar with provenance) → `symbols/Core-MarketData.md` · `Brokers/`
(BrokerKind, options, discovery) → `Core-Brokers.md` · `Strategies/` (ITradingStrategy,
IStrategyFactory, StrategyFactoryRegistration) → `Core-Strategies.md` · `Backtest/` +
`Backtesting/` (engine seams old + P0 rewrite) · `Quant/` (OU, PCA, surfaces, time-series math) ·
`Ml/` (footprint/order-book RLS predictors) · `Regime/`, `IndexKScore/`, `Research/`,
`Configuration/` (AppEdition, BrokerEditionPolicy, DevOptions), `Domain/` (InstrumentId),
`Notifications/`, `AiAnalyst/`, `Trading/`.

**Invariants.** Zero deps (no UI/WPF/SDK types — `verify-on-stop` blocks); provenance fields never
stripped; canonical identity is InstrumentId; new domain types go here, not in consumers.

**Tests** `tests/TradingTerminal.Tests.Headless/` (folders mirror Core's: Quant/, MarketData/, …).

**Common changes.** New option record (pair with broker/DI wiring); new seam member (default-impl
to avoid breaking 12 broker clients); quant math (load `quant-math` skill; numeric tests mandatory).
A public-signature change here = grep-the-world; check `deps.json` first, build full `slnx`.
