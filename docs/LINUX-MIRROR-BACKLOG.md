# Linux mirror backlog

The two trees share zero code, so backend fixes made in `src/windows/` must be replayed by hand
onto `src/linux/`. This file is the running list for the `refactor/windows-scale-pass` branch
(started 2026-07-04): each entry names the Windows-tree change and its Linux-tree counterpart.
Per Dhruv's call, the refactor stays Windows-only and this backlog is replayed later in one sitting.

Tick an entry only after the Linux tree builds green with the replayed change.

## Phase 1 — pipeline channel bounding + log-sink coalescing

- [ ] **`FeedChannel` / `FeedDropMeter` factory** — copy
      `src/windows/Pipeline/TradingTerminal.MarketData/Threading/FeedChannel.cs` to
      `src/linux/Pipeline/TradingTerminal.MarketData/Threading/FeedChannel.cs` (same namespace
      `TradingTerminal.Infrastructure.Threading`). Bounded drop-oldest channels for every
      live-feed bridge + rate-limited drop metering.
- [ ] **`MarketDataRepository`** — `SubscribeBarsAsync` / `SubscribeTicksAsync` bridges switched
      from `Channel.CreateUnbounded` to `FeedChannel.CreateDropOldest` (Bars / Quotes capacities)
      with a `FeedDropMeter` + `LogWarning` on shed. Mirror into
      `src/linux/Pipeline/TradingTerminal.MarketData/MarketDataRepository.cs`.
- [ ] **Broker clients** — same conversion in the Linux copies of:
  - `Infrastructure/Ib/RealIbClient.cs` (bars stream → Bars, trade tape → Trades, nested
    `TickStream.Channel` → Quotes)
  - `Infrastructure/NinjaTrader/RealNinjaClient.cs` (bars → Bars, ticks → Quotes; keep
    `singleWriter: true`)
  - `Infrastructure/CTrader/RealCTraderClient.cs` (bars → Bars, spots → Quotes, depth → Depth;
    keep `singleWriter: true`)
  - `Infrastructure/Alpaca/RealAlpacaClient.cs` (bars → Bars `singleWriter: true`,
    quotes → Quotes multi-writer)
  - `Infrastructure/Upstox/RealUpstoxClient.cs` (nested `Subscription` channel: capacity by
    `StreamKind` — Depth → Depth, else Quotes; creation moves from field initializer into ctor)
  - `Infrastructure/IronBeam/RealIronBeamClient.cs` (nested `Subscription` channel: Trade →
    Trades, Depth → Depth, else Quotes; creation moves into ctor; doc comment updated)
  - `Infrastructure/LondonStrategicEdge/RealLondonStrategicEdgeClient.cs` (nested `Subscription`
    tick channel → Quotes; doc comment updated)
- [ ] **`InMemoryLogSink`** — appends now coalesce through a bounded pending buffer with one
      `UiPost` flush per batch (one dispatcher hop / trim pass / `PropertyChanged` per batch
      instead of per entry). Mirror into
      `src/linux/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs` (public API unchanged;
      the Avalonia head keeps setting `UiPost` to `Dispatcher.UIThread.Post`).

## Later phases

Entries for Phases 2+ are appended here as those phases land backend-touching changes.
UI/XAML work in `src/windows` is WPF-only and is NOT mirrored — the Avalonia parity backlog
tracks UI separately.
