# Signal and pre-trade risk decisions

This file records mechanical choices for the first, backtest-only increment of
`adr-unified-strategy-execution-pipeline.md` D1/D4 and its pre-trade risk increment. The task calls
that increment D6; the accepted ADR's current D6 is the private/public artifact boundary, while exact
coefficient/scale numerics and backtest-first routing are D7. Code cites D6 for ownership and D7 for
the numeric/backtest contract rather than silently changing the accepted ADR's numbering.

## Contract and boundary

- `TradeIntent` is a target-position instruction. The adapter computes `target - actual` immediately
  before submission, so rejections and later signals converge instead of replaying model trades.
- Quantity, price, money, costs, multipliers, and caps are signed `long` coefficient plus decimal
  scale (maximum 18). Exact intermediates use `Int128`. Policy code contains no price/money `double`.
- Exact outputs are canonicalized by removing trailing decimal zeros before narrowing from `Int128`;
  equivalent valid coefficient/scale encodings therefore do not fault merely because an intermediate
  coefficient exceeds `Int64` while the same value has a smaller exact scale.
- Frozen public market/account inputs are binary64. The backtest adapter quantizes bid, ask, equity,
  and contract multiplier once at its boundary to scale 6, midpoint-to-even. Public market orders
  carry only integral `long` quantities. No exact value is silently assigned to a remainder.
- `policyVersion` is supplied by the host at construction, is immutable, and is copied verbatim to
  every accepted intent. Strategy id and signal note id are copied as provenance.

## Unit formulas and rounding

- Conservative default: one fixed contract plus a one-contract buyer cap.
- The policy accepts only the buyer-selected `UnitDefinition`. A seller recommendation is listing
  metadata for the buyer UI and has no execution authority or input channel here.
- Fixed contracts: `floor(fixedContracts * strength)`.
- Percent equity at risk: `floor((equity * basisPoints / 10_000) / perUnitRisk * strength)`.
- Fixed cash risk: `floor(cashBudget / perUnitRisk * strength)`.
- Volatility scaled: `floor(cashBudget / (volatility * multiple * contractMultiplier + costs) * strength)`.
- `perUnitRisk = sizingRiskDistance * contractMultiplier + entry slippage + exit slippage + fees`.
- Risk-budget, per-unit-risk, and strength ratios are cross-reduced before checked multiplication so
  economically equivalent coefficient/scale encodings produce the same target without avoidable
  `Int128` overflow.
- Public signal strength is quantized to millionths, midpoint-to-even, then whole-contract sizing
  rounds toward zero. A zero-strength directional signal is an exit to target zero. A positive
  strength whose exact sizing still floors to zero is likewise an accepted, unprotected flat target;
  cap and protective-price lowering do not turn that zero into an entry.
- Volatility and target multiples are basis-point integers. Multiplication extends the price scale
  by four so fractional results remain exact; trailing decimal zeros are normalized, while a
  result that cannot fit the declared maximum scale faults instead of rounding.

## Fault and cap order

Evaluation order is fixed: signal, provenance, structural inputs, flat exit, market inputs, unit
definition, exact sizing, absolute-unit cap, notional cap, cash-risk cap, protective prices. The
first fault wins. A cap rejection returns its full candidate target with no `TradeIntent`; caps never
clamp. Flat exits bypass stale entry-sizing data so invalid risk settings cannot trap an open target.

## Callback and adapter behavior

- Every market-callback signal is processed in emission order. Before a new target is submitted, an
  unfilled earlier target for the same instrument is cancelled; the replacement delta is computed
  from the settled portfolio. Client ids are deterministic per run (`instrument + sequence`), never
  random or clock-derived.
- A non-flat signal emitted during `OnStart` has no market reference and is rejected observably.
  Signals bind to the active callback instrument; `OnEnd` binds to the primary instrument's last
  reference. The host emits an unreported synthetic Flat decision per instrument at end of run so
  the completed report contains closed round trips.
- Structurally invalid signals are returned to the observer as `InvalidSignal` and are not published
  into the frozen report sink, whose contract rejects them by exception. They never submit an order.
- The frozen backtest router resolves an order by contract symbol only. A multi-instrument universe
  must therefore have case-insensitively unique symbols; the runner rejects an ambiguous universe at
  start rather than allowing a fill to be attributed to the wrong canonical `InstrumentId`.
- The proxy implements `IOrderRouter` only because the public signal sink is router-shaped. Direct
  order attempts from the wrapped signal kernel return `Rejected`; venue fill events are not exposed
  to the signal kernel. Managed order-native kernels continue to run directly and are not wrapped.
- Disposal unbinds the signal proxy before disposing the wrapped kernel. A retained context cannot
  publish report signals, invoke policy, or submit an undrained order after the run has completed.
- The internal execution kernel is constructible only by `SignalBacktestRunner`, which itself owns a
  `BacktestEngine`. This composition remains the original backtest-only venue gate. The later OMS
  slice described below is separate and simulation-only; neither path contains a live router or
  broker adapter.

## Pre-trade risk records and cap semantics

- `RiskPolicy` requires positive exact values for every requested cap. Its lowercase SHA-256 binds a
  length-prefixed policy id/version plus every coefficient and scale in fixed field order. A decision
  copies id, version, hash, limits, full input, reasons, order notional, and before/after exposure;
  `ReplacePolicy` affects only later decisions and the engine's read-only decision view is append-only.
- For a target intent, order quantity is `target - current`; for a delta intent it is the delta and
  projected position is `current + delta`. Order quantity/notional caps use the absolute order delta.
  The position cap uses the projected absolute position. No rejected quantity is clamped.
- Gross exposure after an intent is `gross before - current instrument exposure + projected
  instrument exposure`; this avoids double-counting a replacement target. Filled positions use their
  last exact backtest reference and exact contract multiplier. Pending orders are not exposure until
  the simulated venue fills them.
- Quantity, notional, position, and gross values equal to their caps are admitted. Daily loss equal to
  its cap rejects and latches for that UTC risk day. Invalid or overflowing exact snapshots reject as
  values and never reach the router.
- Risk runs after a complete `TradeIntent` is created and structurally lowered, but before an existing
  pending target is cancelled or a new simulated order is submitted. A rejection is appended and sent
  to the optional risk observer with the complete attempted intent; the replay continues and an older
  already-admitted pending order is left alone because cancel-all is out of scope.
- The source-compatible runner overload creates a fresh per-run policy at the maximum supported exact
  values. Callers that configure real caps pass an explicit `RiskEngine`; both paths still evaluate and
  record every complete intent before the simulated venue. `LastRiskDecisions` exposes the most recently
  started run's records even when that compatibility engine was created internally.

## Daily loss and disarm state

- The engine has no clock. The adapter supplies the UTC `DateOnly` from deterministic market events.
  The first day's opening baseline is run-start equity/mark-to-market; a later day opens from the last
  observed values of the preceding day.
- The public portfolio drops accumulated realized PnL after an instrument becomes flat. Therefore the
  adapter derives exact daily total PnL from quantized opening/current equity, derives mark-to-market
  PnL from each open position's exact latest callback reference, and records realized PnL as the exact
  residual. The public portfolio marks only on quotes, so trade/depth/bar references replace its stale
  unrealized component before risk evaluation. All binary64 values are quantized once at the existing
  scale-6 boundary before risk arithmetic.
- Reaching the combined realized plus mark-to-market daily loss limit latches rejection until a later
  UTC risk-day snapshot. Replacing the policy clears only this daily latch, then re-evaluates the next
  input under the new limit; otherwise a later record could falsely attribute an old trip to the new
  policy. Prior records and the independent manual kill state remain unchanged.
- `TripKillSwitch()` is idempotent and one-way for the run. Once called, every later intent is refused,
  including a flat intent and the runner's synthetic end-of-run flat. It neither cancels working
  orders nor flattens positions; that literal disarm behavior keeps flatten/cancel-all out of scope.

## Protective prices and costs

- When enabled, a protective stop is the sizing-risk distance from the reference price; an optional
  profit target is an exact basis-point multiple of that distance. They are canonical intent data.
- The frozen `IOrderRouter` has no parent/child or bracket/OCO contract. The adapter therefore throws
  before submitting an intent carrying protective prices instead of silently dropping protection or
  inventing unsafe pseudo-OCO behavior. Safe protective-order lowering requires a later reviewed
  seam; it is not a reason to modify or duplicate a public contract in this increment.
- Cost assumptions affect sizing and are recorded on the intent. Actual simulated fees remain the
  `RunSpec.Cost` supplied to `BacktestEngine`; callers keep those two explicit configurations aligned.

## Allocation discipline

`SignalExecutionPolicy.Evaluate` is value-only and allocation-free after construction. The adapter
preallocates its per-instrument maps at start where practical, but an accepted order necessarily
allocates the frozen public `OrderRequest`, `OrderResult` task path, and string client id. No-order
callbacks and policy rejections allocate no execution objects.

## OMS core slice 1 (2026-08-05)

### Domain and public boundary

- The authoritative OMS lives in the internal `TradingTerminal.Execution.Oms` namespace. It wraps
  the existing `TradeIntent`, `RiskEngine`/`RiskPolicy`, and exact `ScaledValues`; it does not create a
  second intent, risk, quantity, price, money, or fee model.
- Every roadmap section 6.2 identity is a distinct value type. `BucketId` and externally assigned
  broker/exchange ids are optional; lease identity and a positive monotonic fencing token are carried
  as domain evidence. Cross-process lease acquisition and fencing transport remain slice 4.
- The public `IOrderRouter` vocabulary is a compatibility boundary, not the authoritative ledger.
  Its integral quantity and binary64 prices are explicitly validated and quantized once when mapped
  into exact values. Rich states with no faithful public representation, especially `Unknown` and
  `Reconciling`, return an explicit mapping fault and are never collapsed to `Rejected`.

### Lifecycle and retry semantics

- The state graph is closed and explicit. A store append whose target is not a legal edge is rejected
  as a value, and each edge is bound to permitted semantic event kinds so a `Prepared` event cannot
  masquerade as validation. Event kinds are also bound to their allowed command, risk, simulated-
  venue, recovery, or reconciliation source. Evidence-only self-state events are limited to facts
  that do not change economic state.
- `SendStarted` is persisted before the simulator is invoked. A proved failure before acceptance moves
  back to `Armed`, and a retry reuses the same `ClientOrderId`. An ambiguous outcome moves to `Unknown`;
  `Unknown` and `Reconciling` block release retry and can advance only through explicit reconciliation.
- Delivery is at-least-once with durable idempotency semantics: retries keep the same `ClientOrderId`
  and inbox key, while conflicting reuse is rejected. This slice makes no exactly-once claim.
- `Prepared` recovery never dispatches automatically. The recovered projection remains `Prepared` and
  requires a new `Armed` event with fresh causation. A fill received while `Acknowledging` is accepted;
  a late acknowledgement is evidence and cannot roll a filled order backward.
- Terminal `Filled`, `Cancelled`, `Rejected`, or `Expired` may receive an explicit reconciliation fact
  and become terminal `Reconciled`. A reconciliation-case record/store seam exists, but no live broker
  query, comparison, or automatic resolution exists in this slice.

### Event ledger, risk, and simulator

- The persistence seam atomically applies inbox deduplication, an append-only per-aggregate sequence,
  a previous-event SHA-256 chain, and an outbox record. Exact duplicate callbacks return the original
  event; a conflicting reuse of a source/dedup key is rejected. The chain detects accidental or silent
  alteration but is not claimed to resist an administrator who can rewrite events and hashes.
- Current state is rebuilt only from ledger events. Fill quantity and fees are accumulated with exact
  coefficient/scale arithmetic; duplicated callbacks cannot double-count economics.
- Any changed replacement terms require a fresh risk snapshot and append a separate versioned risk
  decision before `PendingReplace`; a rejected decision leaves the active terms untouched. The risk
  event binds the exact proposed terms so command replay can resume after either the risk-decision or
  request crash window without re-evaluation. Replacement after a partial fill returns to
  `PartiallyFilled`, not `Working`.
- Validation rejects structurally invalid or unsupported order type/time-in-force terms before arming.
  Accepted and rejected risk decisions are ledger events carrying the existing immutable policy id,
  version, hash, inputs, reasons, and before/after exposure. Risk never clamps a requested order.
- The existing public simulated books were not reused: they are floating-point, omit replace/query and
  required capability semantics, and do not retain terminal idempotency keys; one variant is internal
  and the other would add the broker/network-bearing Infrastructure project. Slice 1 instead uses a
  sealed deterministic in-memory exact-value simulator with submit/cancel/replace/query and durable
  client-id replay. Configured limit and stop-limit fills must honor the exact side-aware limit. It has
  no broker client, socket, network, credential, or live-order implementation.
- SQLite WAL tables, migrations, crash persistence, and restart recovery are deliberately absent. The
  in-memory persistence implementation proves the contract only; durable storage is slice 2.
- The pre-existing allocation-free policy test performs its exact thread-allocation measurement on a
  dedicated warmed thread. This isolates the asserted production call path from test-runner work on a
  shared worker thread without weakening the zero-allocation assertion or serializing the assembly.

## Durable SQLite order ledger slice 2 (2026-08-05)

### File ownership, durability, and migrations

- The durable store uses the repository's existing direct `Microsoft.Data.Sqlite` 9.0.0 convention,
  not Entity Framework or a second SQLite library. One long-lived connection is the serialized writer;
  the database is WAL with foreign keys, a five-second busy timeout, and `synchronous=FULL`. `FULL` is
  an intentional financial-ledger strengthening over the market-data store's loss-tolerant `NORMAL`.
- The injected path may be a test database. The default is the distinct local-app-data file
  `DaxAlgoTerminal/Execution/execution-ledger.db`; it never reuses a market-data path. An SQLite
  application id plus an unexpected-table check refuses a database owned by another subsystem.
- Schema migrations are ordered and forward-only. Version 1 is recorded in both `PRAGMA user_version`
  and `schema_migrations`; it is the first durable format, and a database newer than this binary is
  rejected rather than down-migrated.
  Migration timestamps use the injected UTC clock, and the store otherwise retains the interface's
  caller-supplied recorded time instead of consulting ambient time.

### Authority and exact representation

- `order_events` is the sole economic authority. Each row stores the normalized sequence/state/hash
  envelope plus a lossless versioned JSON payload containing every slice-1 field. JSON numeric tokens
  remain signed integer coefficients and scales; no exact value passes through binary floating point.
  Reload validates the envelope against the payload and then recomputes the existing event hash.
- SQL triggers reject ordinary `UPDATE` or `DELETE` operations on `order_events`. The previous-event
  chain detects accidental or silent corruption, not an administrator who alters rows and recomputes
  every affected hash. Backup integrity does not strengthen that bounded claim.
- A live append first resolves the global `(source, deduplication_key)` inbox identity, then writes the
  event and inbox record, rebuilds that aggregate's projections from its full stream, and creates the
  outbox row in one SQLite transaction. Exact replay returns the original event and changes no economic
  or publication row; conflicting reuse is rejected.
- `orders`, `order_intents`, exact fills/fees, risk decisions, resolution evidence, and fill-backed lot
  facts are disposable projections. The explicit rebuild deletes only those tables and reconstructs
  them from verified `order_events`; it never rewrites the event ledger, inbox, or outbox.

### Domain gaps kept explicit

- Slice 1 has no execution-session identity or operator/configuration audit-event domain. The required
  `execution_sessions` and `audit_events` tables therefore exist as empty forward-compatible shells;
  execution leases are not mislabeled as sessions and order events are not mislabeled as user actions.
- Slice 1 has one aggregate fill fee and no broker fee taxonomy, account-level lot matcher, closing-lot
  allocation, currency, or realized-PnL model. `fees_commissions` stores only the exact supplied fee;
  `position_lots` stores one exact fill-backed fact and makes no FIFO, closing, or profit claim.
- An order event contains only terminal `ReconciliationResolution` evidence. The same SQLite store also
  implements the existing `IReconciliationCaseStore`, preserving the full case kind/status/evidence and
  opened/resolved UTC facts as an append-only sequence protected against SQL update/delete. Integrity
  checks validate each explicit fact and its monotonic progression. Event-derived terminal resolution
  evidence is tagged separately and rebuildable without deleting explicit case facts. Neither path
  performs a venue query.

### Restart recovery and backup

- On open, the store verifies and folds every event stream into an immutable recovery report. Every
  non-terminal order is present; `Prepared` requires fresh authorization, while `Releasing` or
  `Acknowledging` is surfaced with effective state `Unknown`. `CanAdmitNewOrders` is false when this
  startup set is non-empty. Enforcing and clearing the admission gate belongs to the slice-3 coordinator;
  the unchanged `IOrderEventStore` has no recovery-acknowledgement command or honest append fault for it.
- Backup and restore use SQLite's online backup API rather than copying a live `.db` file. Restore writes
  only to a new destination, verifies SQLite plus event/projection integrity, and never replaces an open
  writer in place. The destination name is atomically reserved; a failed copy or validation removes only
  that newly created database and its SQLite sidecars where the operating system permits cleanup.
- The durable store contains no broker adapter, network client, socket, credential, or live routing path.

## Execution coordinator and adapter seam slice 3 (2026-08-05)

### Adapter contract and capability admission

- The formal `IBrokerExecutionAdapter` promotes the slice-1 venue vocabulary rather than creating a
  second order model: canonical instructions, exact values, venue events, native ids, and snapshots
  remain the shared DTOs. `SimulatedExecutionAdapter` composes `DeterministicSimulatedVenue`; it is the
  only implementation and has no broker client, SDK, credential, socket, network, or live-order path.
- Adapter/account identity, session health, data connectivity, execution authentication, and execution
  certification are separate facts. A data-connected but execution-unauthenticated account is valid
  discovery state and returns a clear cannot-execute value; it is never promoted to an executable
  session. The simulator defaults to authenticated and certified only because its venue is in-process.
- Capabilities are immutable and versioned. They expose canonical type/TIF support, exact quantity and
  price precision, minimum/maximum quantity, lot and tick grids, price bands, fractional support,
  replace semantics, native bracket/OCO flags, UTC trading hours, and a fixed-window command budget.
  Exact normalization is pass-through: success returns the original `ScaledValues`; a non-grid,
  out-of-band, unsupported, stale-version, closed-session, or unavailable request is rejected with a
  typed reason. Nothing rounds, clamps, substitutes, or silently downgrades an order. Slice 3 admits
  only in-place replacement; a discovered cancel-and-new capability is rejected until a later slice
  owns the required child-order identity model.
- Coordinator validation performs capability/session admission before risk while the order is Draft,
  and repeats the check immediately before arming. Either failure appends `ValidationRejected`, so no
  unsupported or data-only order can reach `Armed`. A durable store's startup recovery gate is checked
  before both operations; unresolved recovery returns `RecoveryRequired` without running risk or
  arming a new order.

### Release barrier and per-account isolation

- Every adapter/account owns one bounded serial worker. Commands and callbacks for that account enter
  the same queue; different accounts share neither a worker nor a lock. A slow adapter therefore holds
  only its own account path. The slice-3 worker is host-thread driven and creates no timer or background
  racing thread; slice 4 may change hosting without changing coordinator or adapter contracts. Commands
  fail with a value when the bounded queue is saturated; economic callbacks apply bounded per-account
  producer backpressure and are never silently dropped.
- Worker actions are exception-contained so one persistence or callback failure cannot leave an
  account permanently marked as draining. If submit throws after `SendStarted`, the coordinator tries
  to durably move the order to `Unknown`; cancel/replace exceptions after their pending request do the
  same because command acceptance is indeterminate. Failure of that recovery is surfaced as a
  persistence value.
- Release appends `SendStarted` before adapter publication. The adapter returns a deterministic local
  `BrokerDispatchReceipt` and only queues callbacks; it cannot invoke an acknowledgement inline. The
  coordinator then appends `SubmissionRecorded` and only an explicit scheduler drain can deliver the
  external acknowledgement/fill. The seam requires adapters to raise callbacks asynchronously. If a
  non-compliant adapter floods the bounded queue inline, the worker rejects the re-entrant overflow
  instead of deadlocking and the release outcome is treated as unknown.
- The receipt is stored losslessly in the existing `SubmissionRecorded.Reason` payload as a versioned,
  base64-delimited encoding of stable receipt, adapter, account, and command fields. This deliberately
  avoids adding a field to the v1
  `OrderEvent` hash/JSON contract and breaking verification of slice-2 ledgers. The external
  acknowledgement remains a separate `VenueAcknowledged` event, so both facts and their order are
  durable in the existing SQLite event stream.
- A proved pre-dispatch refusal, including submit rate limiting, appends
  `SendFailedBeforeAcceptance` and restores `Armed`; it is not a terminal venue rejection. A proved
  cancel/replace refusal restores `Working` or `PartiallyFilled`. Queue saturation and all adapter
  refusals remain value faults. The simulator does not also schedule a callback for a synchronously
  proved submit refusal, so the coordinator result already contains the durable restored state.

### Asynchronous events and snapshots

- Adapter order and execution events reuse the existing globally deduplicated OMS inbox path. The
  simulator scopes callback keys by adapter/account and uses an injected manually drained FIFO
  scheduler, so duplicates, fill-before-ack, late ids, and fills during pending cancel/replace are
  deterministic test sequences rather than timer races. A partial fill preserves `PendingCancel` or
  `PendingReplace` until its command resolves, and replacement confirmation must exactly match the
  retained risk-authorized terms. Replacement quantity must remain strictly greater than quantity
  already filled.
- Each event subscription captures the publishing adapter/account instead of trusting callback data.
  Callback envelopes and inner venue identities are checked, and an in-process order/account binding
  prevents a malformed adapter from mutating another account's aggregate.
- Commission and position are distinct adapter event categories as required by the broker seam. The
  fee embedded in `FillExecution` remains the sole economic commission input to the OMS ledger; the
  separate commission callback and the position callback are deduplicated ledger evidence events and
  are not counted twice. Position events and snapshots are deterministically fill-derived.
- Query supports exactly one client or broker id. Open-order, completed-order, position, and cash
  snapshots are exposed for a later reconciliation loop, but slice 3 performs no live reconciliation.
  Simulation position snapshots are derived from the same captured order snapshots, so a filled order
  cannot appear without its corresponding position merely because callbacks have not yet been drained.

## Reconciliation engine slice 6 (2026-08-05)

### Cycle, triggers, and exact comparison

- `ReconciliationEngine` is synchronous and timer-free. The host explicitly invokes startup,
  reconnect, periodic, and operator-request cycles; a coordinator constructed with the engine also
  invokes an `UnknownOutcome` cycle immediately when a command or callback leaves an order `Unknown`.
  Every cycle runs on the same bounded serial account worker as commands and callbacks.
- The compared inputs are the OMS event-ledger projections plus the adapter's open-order,
  completed-order, position, and cash snapshot. Orders compare current quantity, cumulative filled
  quantity, limit/stop prices, lifecycle state, instrument, immutable instruction identity/economics,
  current side/type/TIF, and broker/exchange ids. Open/completed collection membership must agree
  with terminality. Positions are rebuilt exactly from signed ledger
  fills. Simulation cash is the exact cumulative price-times-quantity and fee delta in the `SIM`
  denomination from a fixed zero opening baseline; total and available move together because the
  simulator models neither margin nor reserved cash. This is a simulation convention, not a real
  account-currency claim.
- Quantity, price, position, and cash comparisons align decimal scales and require exact numeric
  equality. Equivalent encodings such as `1.0` and `1.00` match; there is zero tick, lot, currency,
  percentage, or binary-floating tolerance.
- Because this slice captures only an in-process simulation snapshot, a snapshot or nested
  position/cash observation older than five seconds (or dated after capture/the cycle) is invalid and
  becomes a durable account `manual_exception`. Automatic clearing additionally requires evidence
  captured strictly after the opening observation. Malformed nested order values are classified the
  same way rather than escaping as an exception.
- The durable classifications are `matched`, `locally_missing`, `broker_missing`,
  `quantity_mismatch`, `price_mismatch`, `terminal_state_mismatch`, `duplicate_candidate`, and
  `manual_exception`. Every subject records separate local and simulated-adapter evidence. Duplicate
  candidates include repeated client ids and repeated broker or exchange ids across either
  local projections or adapter orders; no `Unknown` resolution may proceed through such ambiguity.
  Repeated cycles reuse an unresolved subject/classification case only when both compared evidence documents
  are byte-identical. Changed evidence appends a new opening observation; a later exact comparison
  appends resolution facts for every cleared opening and a new matched observation.

### Materiality, admission, and resolution

- Every classification except `matched` is material. Any unresolved material case closes new
  validation, arming, submit release, and replace for that adapter/account. Cancel remains allowed.
  Claimed reduce-only admission is deliberately also closed: while position truth is disputed, the
  engine cannot prove that a proposed order is reduce-only without crossing or increasing exposure.
  This is the selected fail-closed economic posture.
- Opening observations and later resolutions are separate positive-sequence SQLite rows protected by
  update/delete triggers. A material case must start with an `Open` fact (`matched` starts resolved),
  so an orphan investigation/resolution cannot bypass the gate. A resolution repeats the immutable observation identity/evidence and adds
  UTC time, resolver identity, and resolution evidence; it never rewrites the opening row. Automatic
  exact-match and snapshot resolutions use `system:reconciliation-engine`; explicit resolution
  requires a non-empty operator identity and evidence.
- Case identities hash the account, subject, classification, evidence, cycle token, and observation
  index, then deterministically probe against existing durable ids. Recreating the engine at the same
  injected instant therefore cannot collide with an earlier resolved case.
- An `Unknown` order remains retry-blocked and is a material reconciliation input even if the adapter
  also reports `Unknown`. A unique simulated snapshot can resolve zero-fill `Working`, or terminal
  `Filled`/`Cancelled`/`Rejected`/`Expired`, before the OMS leaves `Unknown`. The snapshot must be
  strictly newer than the last durable `OutcomeUnknown` occurrence and recording time, and its fill
  quantity must be coherent with state and requested quantity. Stale or incoherent evidence never
  moves the order out of `Unknown`; neither does wrong open/completed membership or any independent
  instruction, terms, quantity, price, or conflicting external-id difference. Missing external ids
  are copied onto the reconciliation event before a terminal resolution. A partial-fill snapshot
  lacks individual fill price/fee facts and therefore remains an open manual case rather than
  fabricating economics. No residual is hedged and no corrective order is generated.
- Startup normalizes crash-window `Releasing`/`Acknowledging` projections to `Unknown`, compares all
  single-simulator recovery projections, and keeps the existing recovery gate closed until every
  registered account has completed a startup cycle and its reconciliation gate is clear. Snapshot
  acquisition, cycle orchestration, or account-worker queue failure closes the actual account gate
  until a later successful cycle. Predispatch Draft/Validated/Prepared/Armed orders are deliberately
  excluded because their absence from adapter snapshots is expected, but a durable restart-recovery
  entry in any of those states is not discharged by reconciliation: stale risk/authorization must be
  handled by an explicit later recovery workflow and admission remains closed meanwhile. Durable
  dispatch-receipt account evidence is preferred; a sole simulated account may own otherwise
  unattributed externally visible startup orders. Multi-account `Unknown`/externally visible
  ambiguity remains globally fail-closed.
- The account-bound `ExecutionCoordinator` is the admission surface for this slice. Legacy direct
  `OrderManagementService` methods that drive `DeterministicSimulatedVenue` remain a simulation-only
  slice-1 test seam and are not an operational routing API; they have no account identity with which
  to enforce a per-account reconciliation gate and there is still no live venue path.

### Durable schema and simulation boundary

- SQLite schema version 2 adds account, subject, separate compared evidence, resolver identity, and
  resolution evidence while allowing position/cash subjects without a fake client-order id. Version-1
  explicit case facts cannot truthfully supply those fields, so they remain immutable in
  `reconciliation_cases_v1_legacy`; event-derived negative-sequence resolution projections migrate to
  the rebuilt table. An unresolved material legacy fact closes admission globally after migration;
  successful startup comparison cannot silently bypass it. No legacy account/operator evidence is invented.
- `SimulatedExecutionAdapter.InjectReconciliationSnapshot` is the only divergence-control seam. It
  copies an in-memory snapshot and changes neither venue state nor command dispatch; clearing it
  returns to venue-derived snapshots. The execution assembly still contains no broker client, SDK,
  credential, socket, network, or live-order implementation. This slice adds no service, IPC, lease,
  UI, automatic remediation, or hedging path.

## Out-of-process execution service and fencing slice 4 (2026-08-05)

### Process and local IPC boundary

- `TradingTerminal.Execution.Service` is a separate console process. Its runtime composes the existing
  `OrderManagementService`, `SqliteOrderEventStore`, `ExecutionCoordinator`,
  `SimulatedExecutionAdapter`, and `ReconciliationEngine`; none of their order, ledger, adapter, or
  reconciliation logic is copied into the host. The desktop-facing client API is provided for slice 5,
  but no WPF application or other UI is wired here.
- The only transport is a byte-mode `NamedPipeServerStream`. The factory creates its Windows handle
  with `CreateNamedPipeW`, `PIPE_REJECT_REMOTE_CLIENTS`, `FILE_FLAG_FIRST_PIPE_INSTANCE`, and
  `FILE_FLAG_OVERLAPPED`, then wraps that handle as the managed stream. It applies a protected
  `PipeSecurity` DACL with exactly one allow rule for `WindowsIdentity.GetCurrent().User`; every other
  SID is denied implicitly. An explicit deny-Everyone ACE is intentionally not added because Everyone
  also contains the permitted user and a deny ACE would override the allow. The bundled client exposes
  only the local server name (`.`). There is no TCP, HTTP, WebSocket, generic socket, remote pipe, or
  network fallback.
- Messages are bounded one-MiB JSON frames with a four-byte big-endian length prefix. Protocol v1 has
  request/response plus ordered ledger-event frames. A response declares its exact event count and
  cursor; batches are capped at 256, so a reconnect advances the durable outbox cursor until caught up.
  The payload uses the existing canonical instructions, strongly typed ids, and exact
  coefficient/scale values rather than binary floating point.
- Client disconnect ends only that authenticated connection. It stops further UI-originated commands,
  while the service-owned runtime, simulated working orders, ledger, callback scheduler, and
  reconciliation set remain alive. A reconnect performs a new handshake and `Resync` reads the durable
  outbox. Read-only status/resync remains available after lease loss so working orders are observable;
  no mutating admission is allowed.

### Mutual authentication and version negotiation

- A 32-byte per-service secret is generated on first use and stored at
  `%LOCALAPPDATA%/DaxAlgoTerminal/Execution/service-secret.dpapi`. Only the DPAPI ciphertext is written;
  protection and unprotection use `DataProtectionScope.CurrentUser` plus fixed application entropy.
  Corrupt or wrongly scoped existing state fails closed and is never silently replaced.
- Every connection generates fresh independent 32-byte client and service nonces. Direction-separated
  HMAC-SHA256 transcripts bind both nonces, the requested/service protocol versions, and the version
  decision. Proofs are compared with `CryptographicOperations.FixedTimeEquals`; secret/transcript
  buffers are cleared where owned. The secret itself is never transmitted, and reconnects never reuse
  a proof or bearer credential.
- The service proves possession first, then the client proves possession. A missing, malformed, or bad
  proof closes and logs the connection. Version disagreement is itself covered by both HMAC proofs and
  then returns a clear authenticated mismatch reason; it never falls back to another version.
- Both endpoints impose an intrinsic five-second connection/handshake deadline (injectable in tests).
  The client deadline covers local pipe connection plus mutual proof exchange; the service deadline
  covers the full accepted-connection proof exchange. A silent peer is closed and reported as an
  authentication failure, so it cannot occupy the single pipe instance indefinitely.
- The local authentication boundary is the Windows user account. DPAPI CurrentUser and a one-SID pipe
  DACL exclude other users, but they cannot distinguish the service from malicious code already running
  as that same user, nor from an administrator controlling the machine. No stronger local claim is made.

### Same-machine lease and durable fencing

- Every `BrokerExecutionAccount` maps to a machine-wide named system mutex whose name contains only a
  SHA-256 digest of adapter id plus account id. `Mutex` ownership is thread-affine, so one dedicated
  account thread acquires and holds it for the full lease lifetime; arbitrary pipe/coordinator threads
  never acquire on one thread and release on another. Mutations are marshalled to that owner thread.
- SQLite schema version 3 adds append-only `execution_lease_generations`. After the named mutex is held,
  every acquisition/takeover appends a unique lease id and the prior account token plus one in the same
  durable ledger database. Update/delete triggers forbid rewriting generations. Exhausting signed
  64-bit token space fails closed rather than wrapping. A restart therefore receives a strictly greater
  token than the last committed generation.
- `ExecutionLease.Execute` requires the exact account/lease/token grant, re-reads the latest durable
  generation while the account mutex remains held, and only then invokes the state mutation. Presented
  stale tokens are rejected without invoking the operation. Discovery of a newer durable generation or
  a validation-store failure marks the local lease lost. `MarkLost` flips admission closed before queued
  work can start and releases the mutex after the current fenced operation finishes.
- Lease loss also demotes the stale runtime's SQLite connection to `query_only` and releases the
  ledger's `.writer.lock`. The old service can continue status/resync reads, while a replacement process
  can open the same ledger, acquire the account mutex, and append the next fencing generation without
  waiting for the stale process to exit. The replacement's startup reconciliation owns the existing
  working-order set; the stale service cannot regain write access.
- The fenced coordinator revalidates validation/arming, submit/cancel/replace account-worker actions,
  adapter callback application, Unknown-triggered reconciliation, explicit reconciliation, and startup
  reconciliation. Service orchestration additionally fences draft and prepare at their individual
  commit boundaries. It does not hold the mutex-owner thread while waiting for the coordinator's
  account worker; this avoids an owner/worker lock inversion while each actual mutation, including
  adapter callback application, still revalidates under the same account gate. Existing direct slice-1
  OMS methods remain a simulation test seam, not the service's operational API.
- Exclusivity is deliberately same-machine only. The named pipe, current-user DPAPI secret, and system
  mutex provide no cross-device or cross-host authority. Cross-device/cross-instance exclusivity is
  deferred until a later owner-approved design has a broker or shared authority; this slice does not
  pretend local fencing solves it.

### Simulation-only safety boundary

- The service project references only `TradingTerminal.Execution`; that assembly continues to reference
  Core, Backtest Engine, SQLite, Windows named-pipe access control, and DPAPI. The host constructs only
  `SimulatedExecutionAdapter`, with a fixed exact simulation risk policy and the existing reconciliation
  engine. There is no real broker adapter, venue credential, broker SDK, live-order route, UI, TCP,
  HTTP, WebSocket, socket, or network dependency in this slice.

## Execution console UI slice 5 (2026-08-05)

- `TradingTerminal.ExecutionUi` owns an `IExecutionClient` read-model seam. Its default registration is
  an in-process runtime composed from the existing OMS, in-memory ledger and reconciliation store,
  execution lease/fence, coordinator, service engine, and `SimulatedExecutionAdapter`. The named-pipe
  implementation remains an alternate future backing; the UI truthfully labels the active default as
  `in-process · Simulated` rather than claiming a service process was spawned.
- Book names, configured routing-set chips, deterministic model reference values, display P&L/marks,
  and escalation disclosure are UI configuration/read-model facts because the OMS has no book,
  routing-set, mark-to-market, lot, or escalation domain. Broker names never select an adapter; real
  quantity and order/lease/reconciliation/ledger state come from the simulation runtime.
- The representative Alpha book starts with two deliberately injected simulation-only divergences.
  Existing fail-closed reconciliation semantics therefore show `Gate blocked`; operator reconciliation
  clears the injected snapshot and must reopen the exact gate before a confirmed flatten can submit
  ordinary fenced simulated intents. Pause is a client-local intake gate over a seam that exposes no
  ordinary submit method, not the irreversible risk kill switch. A flatten attempt sets that gate first,
  leaves it paused on every failure path, requires each simulated order to finish `Filled`, and verifies
  the final adapter snapshot has zero open positions before reporting success.
- The visible event ledger is a 96-entry drop-oldest ring advanced through bounded service resync
  batches. The VM turns invalidations into a dirty flag consumed by `UiThread.CreateRenderTimer`; it
  never marshals per event. The shell resolves the hosted tool from a per-window service scope. Closing
  it unsubscribes the client event, disposes the timer, coordinator, lease, and scope, and clears the
  retained snapshots and ledger view.
- This slice adds no broker SDK/client, credential, TCP/HTTP/WebSocket, socket, remote pipe, or live-order
  route. Any non-simulated implementation remains owner-gated work outside this decision.

## Alpaca PAPER execution adapter and manual test ticket (2026-08-06)

### Endpoint, opt-in, and credential boundary

- Alpaca execution is registered only when `AlpacaExecution:Enabled` is explicitly true; its default
  is false, so the ordinary application composition retains only the Simulated execution path. The
  central endpoint gate accepts exactly `https://paper-api.alpaca.markets` for trading and
  `https://data.alpaca.markets` for the optional latest-trade risk mark. The live
  `https://api.alpaca.markets` origin throws before the transport factory or `HttpClient` can run.
  `AllowLiveExecution` also defaults false; setting it true changes the error to "not implemented"
  and still throws. It is a reserved gate, not a route.
- The PAPER key id and secret are supplied by local configuration/environment binding or the broker
  card at runtime. The card holds them only in memory and does not write configuration. The reusable
  `HttpClient`, credential headers, JSON parsing, and URI-origin checks are confined to the
  `Alpaca/` adapter folder. Production redirects are disabled and every returned request URI is
  checked against the approved PAPER/data origins.

### Native capability and exact-value boundary

- Connection authenticates `GET /v2/account` and discovers the configured asset with
  `GET /v2/assets/{symbol}`. Native discovery reports the Alpaca order types, time-in-force values,
  `us_equity`/`crypto` class, fractional/notional flags, and asset minimum/increment/tick values.
  The shared canonical OMS remains whole-quantity only, so fractional and notional support are
  reported but not advertised to canonical admission. Equity maps the exact canonical market,
  limit, stop, stop-limit and day/GTC/IOC/FOK subset; crypto maps only market, limit, stop-limit and
  GTC/IOC. Trailing-stop, OPG, and CLS remain native-only because the canonical domain cannot express
  them. No term is rounded, substituted, or silently downgraded.
- Crypto remains discoverable but execution-uncertified in this slice. The polled order entity does
  not provide exact fee or maker/taker evidence, so a crypto fill cannot yet satisfy the ledger's
  exact `FillExecution` contract. Certified US-equity PAPER fills use the venue's commission-free
  paper invariant; the required liquidity field is conservatively recorded as taker and has no fee
  effect. Exact crypto fee/activity ingestion is deferred rather than fabricating zero fees.
- Decimal strings cross the transport as exact `ScaledValues`. Non-representable quantities,
  prices, fills, assets, order states, or snapshots fail closed. `client_order_id` is the canonical
  idempotency key and is bounded to Alpaca's 48-character limit.

### Commands, updates, and reconciliation

- Trading API v2 operations are `POST /v2/orders`, `DELETE /v2/orders/{id}`, and
  `PATCH /v2/orders/{id}`; lookup supports broker id and `client_order_id`. Reconciliation reads
  bounded open/closed order pages, `GET /v2/positions`, and `GET /v2/account`. A page at the configured
  500-order ceiling is treated as potentially truncated and fails closed instead of comparing partial
  account truth.
- Production uses a one-second, bounded `GET /v2/orders?status=all` polling source rather than the
  trade-updates WebSocket. This keeps one injected event seam for deterministic tests and avoids a
  second connection lifecycle in the first PAPER slice. Fingerprints, adapter correlations, callback
  scheduling, command rate, and manual-ticket orders are bounded. The adapter, poller, scheduler,
  connection cancellation sources, timer, pending operations, and reusable HTTP transport have both
  synchronous and asynchronous disposal paths.
- A full 500-row polling page is still processed so recent acknowledgements/fills are not dropped,
  then degrades the session to close new admission. A 500-row reconciliation page is potentially
  incomplete and is refused. This first slice intentionally fails closed instead of adding an
  unbounded historical pagination loop.
- A real-account reconciliation cash basis starts from the authenticated opening currency/cash and
  then applies exact ledger fill/fee deltas. Alpaca buying power is retained as observed available
  value but is not compared as derivable cash because it is margin-policy output. Positions retain a
  zero local basis, and broker orders/history absent from the local ledger remain material cases. A
  dedicated PAPER account is therefore the safe expected owner workflow.

### Execution-console route

- The data-driven Alpaca broker card accepts runtime paper credentials and reports PAPER connected,
  execution-authenticated, data-only, or authentication-error state. Once connected, one book can
  attach the existing lease, OMS, coordinator, ledger, and reconciliation engine to that adapter; no
  execution subsystem is forked.
- The compact PAPER/TEST ticket uses the selected book and configured instrument, side, whole
  quantity, and market/limit price terms. Limit orders use the exact entered price for risk. Market
  orders refresh the optional latest trade and refuse dispatch unless its UTC observation is no more
  than 15 seconds old. A send becomes a `TradeIntent`, passes the existing risk/lease/reconciliation
  gates, releases through the coordinator to Alpaca PAPER, and returns adapter acknowledgements,
  fills, position evidence, and ledger facts through the existing callback path. There is deliberately
  no live endpoint, payment-like confirmation, default Alpaca registration, or real-network test.
- Ticket `client_order_id` values combine a cryptographically random bounded runtime namespace with
  a monotonic sequence, remain below Alpaca's 48-character limit, and therefore do not repeat merely
  because the UI process or its in-memory book numbering restarted.

## Paper-default, authorization-gated LIVE execution foundation (2026-08-06)

### Mode and persisted authorization

- `ExecutionMode` is attached to each broker adapter connection and is `Paper` by default. The
  Simulated adapter reports only `Paper` and has no mode-change path. A book inherits its attached
  adapter's mode; mode changes reconstruct a disconnected, unbound adapter rather than mutating an
  endpoint beneath an active connection or execution lease.
- A LIVE endpoint token can be constructed only when the broker-specific `AllowLiveExecution` option
  is true, structurally complete credentials are present, and a persisted confirmation matches the
  exact mode-neutral broker plus account ID. The confirmation must contain the exact ordinal text
  `LIVE`, a UTC timestamp, and a bounded current-user identity. Missing, malformed, corrupt,
  oversized, or mismatched state fails closed before transport construction.
- The production confirmation store is bounded to 64 records and protected with Windows DPAPI
  `CurrentUser` at a dedicated local-app-data path. Writes use a flushed temporary file and atomic
  replacement. No credential is included in the confirmation document, UI read models, or messages.

### cTrader and Alpaca certification

- cTrader binds `Paper` to the exact DEMO host and `Live` to the exact LIVE host. LIVE construction
  uses the shared three-part gate; connection certification additionally requires the authenticated
  cTrader account to report `IsLive=true` for LIVE and `IsLive=false` for Paper.
- Alpaca binds `Paper` and `Live` to their exact trading API origins. LIVE construction requires an
  exact expected account ID in the persisted confirmation, and connection certification requires the
  authenticated account ID to match it exactly. Runtime LIVE credentials must be identical to the
  credentials that passed endpoint construction. A key's environment cannot be inferred locally;
  broker authentication at the exact gated endpoint is therefore the final credential/environment
  proof, and no order can execute before that certification succeeds.
- Both modes use the same adapter, OMS, reconciliation, risk, command-rate, lease, and fencing paths.
  The mode never bypasses an admission decision. LIVE authorization is re-read before Connect and
  every Submit, Cancel, or Replace; revoking or corrupting confirmation closes the session and rejects
  before command dispatch. In addition, every LIVE Submit, Cancel, or Replace must consume an opaque,
  one-use coordinator admission bound to the exact account, operation, causation, and payload. The
  coordinator creates that admission only inside the account worker after the applicable risk,
  current lease/fencing, and reconciliation checks; direct adapter calls fail before transport
  dispatch. Raw production transport interfaces are not DI-resolvable. Tests inject mock transports
  for both modes and do not contact either venue.

### Operator surface and lifecycle

- An app-lifetime bounded status projection keeps a red global LIVE banner in the main shell even
  while the execution console is closed whenever a configured broker or attached book is LIVE;
  projection faults also fail red. The console reuses that truth and shows explicit mode badges on
  broker and book rows; otherwise both surfaces show a calm PAPER indicator. Switching to LIVE
  requires the disconnected broker, exact account binding, and an ordinal typed `LIVE` dialog; only
  the backend persists it and constructs the endpoint after the remaining gates pass.
- The manual reconciliation button is absent; reconciliation remains an automatic admission step.
  One Start/Stop control changes new-order intake only. The confirm-gated Kill action stops intake,
  serializes against ticket commands, cancels cancellable working OMS orders, waits up to ten seconds
  for exact terminal cancellation evidence, and only then submits flattening through the same
  reconciliation, lease/fence, risk, and adapter path. Unverified cancellation leaves intake stopped
  and sends no flatten order. The ticket uses the shared bounded `InstrumentPicker` catalog.
- Mode reconstruction owns and disposes every transport, polling source, scheduler, event
  subscription, and cancellation source it creates. The UI retains the existing disposed refresh
  timer and coalesced invalidation model; no per-event UI marshal or unbounded collection is added.
  Console instances share a bounded process-wide fencing-generation store, and unique lease IDs keep
  reopened consoles monotonic while the process is alive.
