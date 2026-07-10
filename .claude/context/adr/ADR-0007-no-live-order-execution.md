# ADR-0007 — Data/signals only: no live order execution

**Status** accepted (execution path removed in c88e71f)

**Context.** Live order routing carries regulatory, safety, and testing burdens far beyond a
signals terminal's scope.

**Decision.** The terminal ingests market data and emits signals/analytics only. The
`IOrderRouter` seam exists solely inside the backtest engine (simulated fills). The live
order-execution path was removed.

**Consequences.** Never add a live order path or wire `IOrderRouter` to a real broker. Broker
integrations implement market-data members of `IBrokerClient` only. If live trading ever
returns, re-tie LiveOrderRouter to `InstrumentId.Source` (see project_order_routing_followup).
