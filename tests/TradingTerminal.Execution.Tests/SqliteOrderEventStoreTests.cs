using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;
using OmsOrderEvent = TradingTerminal.Execution.Oms.OrderEvent;

namespace TradingTerminal.Execution.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteOrderEventStoreCollection
{
    public const string Name = nameof(SqliteOrderEventStoreCollection);
}

[Collection(SqliteOrderEventStoreCollection.Name)]
public sealed class SqliteOrderEventStoreTests
{
    private static readonly string[] RequiredTables =
    [
        "execution_sessions",
        "order_intents",
        "orders",
        "order_events",
        "fills",
        "fees_commissions",
        "position_lots",
        "risk_decisions",
        "reconciliation_cases",
        "reconciliation_cases_v1_legacy",
        "audit_events",
        "inbox_dedupe",
        "outbox",
        "execution_lease_generations",
    ];

    [Fact]
    public void CurrentSchemaVersion_CreatesRequiredTablesAndUsesWal()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("schema.db");
        using var store = new SqliteOrderEventStore(databasePath, Clock());

        Assert.Equal(4, store.SchemaVersion);
        Assert.Equal("wal", store.JournalMode.ToLowerInvariant());
        Assert.Equal(Path.GetFullPath(databasePath), store.DatabasePath);

        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        var tables = ReadStrings(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");
        foreach (var table in RequiredTables)
            Assert.Contains(table, tables);
        Assert.Equal(4L, ScalarInt64(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;"));
        Assert.Equal(1L, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;"));
        Assert.Equal(1L, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 3;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreContract_ExactAppendReplayCreatesOneEventAndOutboxEntry(bool durable)
    {
        using var harness = StoreHarness.Create(durable);
        var instruction = ExactInstruction(durable ? "contract-sqlite" : "contract-memory");
        var draft = DraftCreated(instruction, "contract");
        var firstRecordedAt = OmsTestData.TimestampUtc.AddTicks(1);

        var first = harness.Store.Append(draft, firstRecordedAt);
        var replay = harness.Store.Append(draft, firstRecordedAt.AddMinutes(5));

        Assert.True(first.WasAppended);
        Assert.True(replay.IsExactReplay);
        Assert.Equal(first.Event, replay.Event);
        Assert.Equal(firstRecordedAt, replay.Event!.RecordedAtUtc);
        Assert.Single(harness.Store.Read(instruction.Identity.ClientOrderId));
        Assert.Single(harness.Store.ReadOutbox());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreContract_ConflictAndIllegalTransitionLeaveLedgerUnchanged(bool durable)
    {
        using var harness = StoreHarness.Create(durable);
        var instruction = ExactInstruction(durable ? "atomic-sqlite" : "atomic-memory");
        var draft = DraftCreated(instruction, "atomic");
        Assert.True(harness.Store.Append(draft, OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);

        var conflict = harness.Store.Append(
            draft with { Reason = "different content under the same inbox identity" },
            OmsTestData.TimestampUtc.AddMinutes(1));
        var illegal = harness.Store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.Armed,
                OrderLifecycleState.Armed,
                OrderEventSource.Command,
                OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-illegal"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-illegal")),
            OmsTestData.TimestampUtc.AddTicks(1));

        Assert.Equal(OrderEventAppendFault.ConflictingDuplicate, conflict.Fault);
        Assert.Equal(OrderEventAppendFault.IllegalTransition, illegal.Fault);
        Assert.Single(harness.Store.Read(instruction.Identity.ClientOrderId));
        Assert.Single(harness.Store.ReadOutbox());
        Assert.Empty(harness.Store.ReadOutbox(1));
    }

    [Fact]
    public void FullOmsRoundTrip_ReloadsExactEventsAndRebuildsProjectionFromLedger()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("roundtrip.db");
        var instruction = ExactInstruction("roundtrip");
        var firstFill = new FillExecution(
            new ScaledQuantity(100, 2),
            new ScaledPrice(100_000, 3),
            new ScaledMoney(2_500, 4),
            LiquidityFlag.Maker);
        var secondFill = new FillExecution(
            new ScaledQuantity(1_000, 3),
            new ScaledPrice(1_010_000, 4),
            new ScaledMoney(5_000, 4),
            LiquidityFlag.Taker);
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [firstFill, secondFill]);
        OmsOrderEvent[] expectedEvents;
        OrderProjection expectedProjection;

        using (var store = new SqliteOrderEventStore(databasePath, Clock()))
        {
            var service = Service(store, [plan]);
            Arm(service, instruction);

            var released = service.Release(
                instruction.Identity.ClientOrderId,
                Context(instruction, "release"));

            Assert.True(released.IsSuccess);
            Assert.Equal(OrderLifecycleState.Filled, released.Projection!.State);
            expectedProjection = released.Projection;
            expectedEvents = service.ReadEvents(instruction.Identity.ClientOrderId).ToArray();
            Assert.True(store.VerifyIntegrity().IsValid);
        }

        using var reopened = new SqliteOrderEventStore(databasePath, Clock());
        var reloadedEvents = reopened.Read(instruction.Identity.ClientOrderId).ToArray();
        Assert.Equal(expectedEvents, reloadedEvents);
        var reloadedInstruction = Assert.IsType<CanonicalOrderInstruction>(reloadedEvents[0].Instruction);
        Assert.Equal(instruction, reloadedInstruction);
        Assert.Equal(new ScaledQuantity(20, 1), reloadedInstruction.TradeIntent.SignedUnits);
        Assert.Equal(new ScaledQuantity(200, 2), reloadedInstruction.Terms.Quantity);
        Assert.Equal(new ScaledMoney(123_450, 4), reloadedInstruction.TradeIntent.EstimatedRoundTripCostPerUnit);
        Assert.Equal(
            new ScaledPrice(100_000, 3),
            Assert.Single(reloadedEvents, item => item.Kind == OrderEventKind.RiskAccepted)
                .RiskDecision!.Value.Input.ReferencePrice);
        Assert.Equal(
            new[] { firstFill, secondFill },
            reloadedEvents.Where(item => item.Kind == OrderEventKind.FillReceived)
                .Select(item => item.Fill!.Value)
                .ToArray());

        var pureReplay = OrderProjection.Rebuild(reloadedEvents);
        Assert.True(pureReplay.IsSuccess);
        Assert.Equal(expectedProjection, pureReplay.Projection);
        Assert.Equal(expectedProjection, reopened.ReadProjection(instruction.Identity.ClientOrderId));

        Execute(databasePath, "DELETE FROM orders;");
        Assert.Null(reopened.ReadProjection(instruction.Identity.ClientOrderId));
        Assert.Equal(1, reopened.RebuildProjections());
        Assert.Equal(expectedProjection, reopened.ReadProjection(instruction.Identity.ClientOrderId));
        Assert.True(reopened.VerifyIntegrity().IsValid);
    }

    [Fact]
    public void DuplicateVenueCallbackAcrossReopen_DoesNotDoubleCountEconomics()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("dedupe.db");
        var instruction = ExactInstruction("dedupe-reopen");
        var fill = new FillExecution(
            new ScaledQuantity(2_000, 3),
            new ScaledPrice(100_000, 3),
            new ScaledMoney(7_000, 3),
            LiquidityFlag.Taker);
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [fill]);
        VenueEvent duplicateCallback;
        int eventCount;
        int outboxCount;

        using (var store = new SqliteOrderEventStore(databasePath, Clock()))
        {
            var clock = Clock();
            var venue = new DeterministicSimulatedVenue(clock, [plan]);
            var service = new OrderManagementService(store, OmsTestData.RiskEngine(), venue, clock);
            Arm(service, instruction);
            Assert.True(service.Release(
                instruction.Identity.ClientOrderId,
                Context(instruction, "release")).IsSuccess);

            var replayedSubmit = venue.Submit(instruction, OmsTestData.Causation("dedupe-replayed-submit"));
            Assert.Equal(VenueCommandStatus.IdempotentReplay, replayedSubmit.Status);
            duplicateCallback = Assert.Single(replayedSubmit.Events, item => item.Kind == VenueEventKind.Fill);
            eventCount = store.Read(instruction.Identity.ClientOrderId).Count;
            outboxCount = store.ReadOutbox().Count;
        }

        using var reopened = new SqliteOrderEventStore(databasePath, Clock());
        var reopenedClock = Clock();
        var reopenedService = new OrderManagementService(
            reopened,
            OmsTestData.RiskEngine(),
            new DeterministicSimulatedVenue(reopenedClock),
            reopenedClock);

        var duplicate = reopenedService.ApplyVenueEvent(duplicateCallback);

        Assert.True(duplicate.IsSuccess);
        Assert.Equal(eventCount, reopened.Read(instruction.Identity.ClientOrderId).Count);
        Assert.Equal(outboxCount, reopened.ReadOutbox().Count);
        Assert.Equal(new ScaledQuantity(2, 0), duplicate.Projection!.FilledQuantity);
        Assert.Equal(new ScaledMoney(7, 0), duplicate.Projection.TotalFees);
        Assert.True(reopened.VerifyIntegrity().IsValid);
    }

    [Fact]
    public void TamperedOrderEventPayload_IsDetectedAfterAppendOnlyTriggerIsExplicitlyDropped()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("tampered.db");
        var instruction = ExactInstruction("tampered");
        using var store = new SqliteOrderEventStore(databasePath, Clock());
        Assert.True(store.Append(
            DraftCreated(instruction, "tampered"),
            OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);

        Execute(
            databasePath,
            """
            DROP TRIGGER order_events_no_update;
            UPDATE order_events
            SET event_payload_json = json_set(event_payload_json, '$.Reason', 'tampered payload')
            WHERE aggregate_id = 'tampered' AND aggregate_sequence = 1;
            """);

        var integrity = store.VerifyIntegrity();

        Assert.False(integrity.IsValid);
        Assert.Equal(SqliteOrderLedgerIntegrityFault.EventChainInvalid, integrity.Fault);
        Assert.Equal(instruction.Identity.ClientOrderId, integrity.AggregateId);
        Assert.Contains(nameof(OrderEventChainFault.EventHashMismatch), integrity.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopen_ReportsPreparedAndReleasingRecoveryRequirements()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("recovery.db");
        var preparedInstruction = ExactInstruction("prepared-recovery");
        var releasingInstruction = ExactInstruction("releasing-recovery");

        using (var store = new SqliteOrderEventStore(databasePath, Clock()))
        {
            var service = Service(store);
            Prepare(service, preparedInstruction);
            Arm(service, releasingInstruction);
            var sendContext = Context(releasingInstruction, "crash-send");
            Assert.True(store.Append(
                new OrderEventDraft(
                    releasingInstruction.Identity.ClientOrderId,
                    OrderEventKind.SendStarted,
                    OrderLifecycleState.Releasing,
                    OrderEventSource.Command,
                    sendContext.DeduplicationKey.Derive("send-started"),
                    OmsTestData.TimestampUtc,
                    sendContext.CausationId),
                OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);
        }

        using var reopened = new SqliteOrderEventStore(databasePath, Clock());

        Assert.False(reopened.CanAdmitNewOrders);
        Assert.False(reopened.CanAdmitAfterStartupReconciliation);
        Assert.Collection(
            reopened.RecoverySet,
            prepared =>
            {
                Assert.Equal(preparedInstruction.Identity.ClientOrderId, prepared.ClientOrderId);
                Assert.Equal(OrderLifecycleState.Prepared, prepared.PersistedState);
                Assert.Equal(OrderLifecycleState.Prepared, prepared.EffectiveRecoveryState);
                Assert.Equal(OrderRecoveryRequirement.FreshAuthorizationRequired, prepared.Requirement);
            },
            releasing =>
            {
                Assert.Equal(releasingInstruction.Identity.ClientOrderId, releasing.ClientOrderId);
                Assert.Equal(OrderLifecycleState.Releasing, releasing.PersistedState);
                Assert.Equal(OrderLifecycleState.Unknown, releasing.EffectiveRecoveryState);
                Assert.Equal(OrderRecoveryRequirement.OutcomeUnknown, releasing.Requirement);
            });
    }

    [Fact]
    public void EmptyVersionZeroDatabase_MigratesForwardOnce_AndNewerVersionIsRejected()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("migration.db");
        using (var empty = Open(databasePath, SqliteOpenMode.ReadWriteCreate))
            Assert.Equal(0L, ScalarInt64(empty, "PRAGMA user_version;"));

        using (var migrated = new SqliteOrderEventStore(databasePath, Clock()))
            Assert.Equal(4, migrated.SchemaVersion);
        using (var reopened = new SqliteOrderEventStore(databasePath, Clock()))
            Assert.Equal(4, reopened.SchemaVersion);
        using (var connection = Open(databasePath, SqliteOpenMode.ReadOnly))
            Assert.Equal(4L, ScalarInt64(connection, "SELECT COUNT(*) FROM schema_migrations;"));

        var futurePath = directory.File("future.db");
        using (var future = Open(futurePath, SqliteOpenMode.ReadWriteCreate))
            Execute(future, "PRAGMA user_version = 5;");
        Assert.Throws<NotSupportedException>(() => new SqliteOrderEventStore(futurePath, Clock()));
    }

    [Fact]
    public void VersionOneUnresolvedCase_RemainsFailClosedAfterVersionTwoMigration()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("legacy-reconciliation-case.db");
        using (var initialized = new SqliteOrderEventStore(databasePath, Clock()))
            Assert.Equal(4, initialized.SchemaVersion);

        Execute(databasePath, $"""
            DROP TRIGGER reconciliation_case_facts_no_update;
            DROP TRIGGER reconciliation_case_facts_no_delete;
            DROP TRIGGER reconciliation_case_v1_legacy_no_update;
            DROP TRIGGER reconciliation_case_v1_legacy_no_delete;
            DROP INDEX ix_reconciliation_cases_order;
            DROP INDEX ix_reconciliation_cases_account;
            DROP TABLE reconciliation_cases;
            ALTER TABLE reconciliation_cases_v1_legacy RENAME TO reconciliation_cases;
            CREATE INDEX ix_reconciliation_cases_order
                ON reconciliation_cases(client_order_id, case_id, fact_sequence);
            CREATE TRIGGER reconciliation_case_facts_no_update
            BEFORE UPDATE ON reconciliation_cases
            WHEN OLD.fact_sequence > 0 OR NEW.fact_sequence > 0
            BEGIN
                SELECT RAISE(ABORT, 'reconciliation case facts are append-only');
            END;
            CREATE TRIGGER reconciliation_case_facts_no_delete
            BEFORE DELETE ON reconciliation_cases
            WHEN OLD.fact_sequence > 0
            BEGIN
                SELECT RAISE(ABORT, 'reconciliation case facts are append-only');
            END;
            DELETE FROM schema_migrations WHERE version = 2;
            DELETE FROM schema_migrations WHERE version = 3;
            DELETE FROM schema_migrations WHERE version = 4;
            ALTER TABLE order_intents DROP COLUMN entry_limit_coefficient;
            ALTER TABLE order_intents DROP COLUMN entry_limit_scale;
            ALTER TABLE order_intents DROP COLUMN entry_stop_coefficient;
            ALTER TABLE order_intents DROP COLUMN entry_stop_scale;
            DROP TRIGGER execution_lease_generations_no_update;
            DROP TRIGGER execution_lease_generations_no_delete;
            DROP INDEX ix_execution_lease_generations_latest;
            DROP TABLE execution_lease_generations;
            PRAGMA user_version=1;
            INSERT INTO reconciliation_cases(
                case_id, fact_sequence, client_order_id, kind, status, evidence,
                opened_at_utc_ticks, resolved_at_utc_ticks,
                source_order_sequence, observed_state, source_event_hash)
            VALUES (
                'legacy-unresolved', 1, 'legacy-order', 7, 0, 'legacy evidence',
                {OmsTestData.TimestampUtc.Ticks}, NULL, NULL, NULL, NULL);
            """);

        using var migrated = new SqliteOrderEventStore(databasePath, Clock());

        Assert.Equal(4, migrated.SchemaVersion);
        Assert.False(migrated.CanAdmitAfterStartupReconciliation);
        Assert.False(migrated.CanAdmitNewOrders);
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1L, ScalarInt64(
            connection,
            "SELECT COUNT(*) FROM reconciliation_cases_v1_legacy WHERE case_id = 'legacy-unresolved';"));
    }

    [Fact]
    public void BackupAndRestore_PreserveEventsOutboxProjectionAndIntegrity()
    {
        using var directory = new TestDirectory();
        var sourcePath = directory.File("source.db");
        var backupPath = directory.File("backup.db");
        var restoredPath = directory.File("restored.db");
        var instruction = ExactInstruction("backup-restore");
        OmsOrderEvent[] expectedEvents;
        OrderEventOutboxEntry[] expectedOutbox;
        OrderProjection expectedProjection;

        using (var source = new SqliteOrderEventStore(sourcePath, Clock()))
        {
            Assert.True(source.Append(
                DraftCreated(instruction, "backup"),
                OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);
            expectedEvents = source.Read(instruction.Identity.ClientOrderId).ToArray();
            expectedOutbox = source.ReadOutbox().ToArray();
            expectedProjection = source.ReadProjection(instruction.Identity.ClientOrderId)!;
            source.BackupTo(backupPath);
        }

        Assert.True(File.Exists(backupPath));
        SqliteOrderEventStore.RestoreDatabase(backupPath, restoredPath, Clock());

        using var restored = new SqliteOrderEventStore(restoredPath, Clock());
        Assert.Equal(expectedEvents, restored.Read(instruction.Identity.ClientOrderId).ToArray());
        Assert.Equal(expectedOutbox, restored.ReadOutbox().ToArray());
        Assert.Equal(expectedProjection, restored.ReadProjection(instruction.Identity.ClientOrderId));
        Assert.True(restored.VerifyIntegrity().IsValid);
    }

    [Fact]
    public void SharedIntentIdAcrossClientOrders_RemainsAValidMultiLegStreamSet()
    {
        using var directory = new TestDirectory();
        using var store = new SqliteOrderEventStore(directory.File("shared-intent.db"), Clock());
        var sharedIntentId = new IntentId("shared-economic-intent");
        var first = ExactInstruction("shared-intent-leg-1");
        first = first with { Identity = first.Identity with { IntentId = sharedIntentId } };
        var second = ExactInstruction("shared-intent-leg-2");
        second = second with { Identity = second.Identity with { IntentId = sharedIntentId } };

        Assert.True(store.Append(DraftCreated(first, "shared"), OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);
        Assert.True(store.Append(DraftCreated(second, "shared"), OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);

        Assert.Single(store.Read(first.Identity.ClientOrderId));
        Assert.Single(store.Read(second.Identity.ClientOrderId));
        Assert.True(store.VerifyIntegrity().IsValid);
    }

    [Fact]
    public void SecondDurableWriterForSameFile_IsRejected()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("single-writer.db");
        using var first = new SqliteOrderEventStore(databasePath, Clock());

        var exception = Assert.Throws<IOException>(() => new SqliteOrderEventStore(databasePath, Clock()));

        Assert.Contains("writer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizedProjectionAndOutboxTampering_AreReportedByIntegrityCheck()
    {
        using var directory = new TestDirectory();
        var projectionPath = directory.File("projection-tamper.db");
        var instruction = ExactInstruction("projection-tamper");
        using (var store = new SqliteOrderEventStore(projectionPath, Clock()))
        {
            Assert.True(store.Append(
                DraftCreated(instruction, "projection-tamper"),
                OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);
            Execute(projectionPath, "UPDATE orders SET state = 99 WHERE client_order_id = 'projection-tamper';");

            Assert.Equal(
                SqliteOrderLedgerIntegrityFault.ProjectionMismatch,
                store.VerifyIntegrity().Fault);
        }

        var outboxPath = directory.File("outbox-tamper.db");
        var outboxInstruction = ExactInstruction("outbox-tamper");
        using var outboxStore = new SqliteOrderEventStore(outboxPath, Clock());
        Assert.True(outboxStore.Append(
            DraftCreated(outboxInstruction, "outbox-tamper"),
            OmsTestData.TimestampUtc.AddTicks(1)).IsSuccess);
        Execute(outboxPath, "UPDATE outbox SET event_hash = 'incorrect';");

        Assert.Equal(
            SqliteOrderLedgerIntegrityFault.DeliveryMetadataInvalid,
            outboxStore.VerifyIntegrity().Fault);
    }

    [Fact]
    public void ReconciliationCaseFacts_AppendAndReloadThroughExistingSeam()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("reconciliation-cases.db");
        var opened = new ReconciliationCase(
            new ReconciliationCaseId("case-durable-1"),
            new BrokerExecutionAccount(
                new ExecutionAdapterId("simulated"),
                new BrokerAccountId("case-account")),
            ReconciliationSubjectKind.Order,
            "case-order-1",
            new ClientOrderId("case-order-1"),
            ReconciliationCaseKind.ManualException,
            ReconciliationCaseStatus.Open,
            "Local send state is Unknown.",
            "Simulated snapshot state is Unknown.",
            OmsTestData.TimestampUtc,
            null,
            null,
            null);
        var resolved = opened with
        {
            Status = ReconciliationCaseStatus.Resolved,
            ResolvedAtUtc = OmsTestData.TimestampUtc.AddMinutes(1),
            ResolvedBy = "operator:test",
            ResolutionEvidence = "Simulator evidence proved the terminal outcome.",
        };

        using (var store = new SqliteOrderEventStore(databasePath, Clock()))
        {
            IReconciliationCaseStore caseStore = store;
            Assert.False(caseStore.TryAppend(resolved with
            {
                CaseId = new ReconciliationCaseId("case-orphan-resolution"),
            }));
            Assert.False(caseStore.TryAppend(opened with
            {
                CaseId = new ReconciliationCaseId("case-orphan-investigation"),
                Status = ReconciliationCaseStatus.Investigating,
            }));
            Assert.True(caseStore.TryAppend(opened));
            Assert.True(caseStore.TryAppend(resolved));
            Assert.True(caseStore.TryAppend(resolved));
            Assert.Equal(new[] { opened, resolved }, caseStore.Read(opened.ClientOrderId!.Value));
            Assert.True(store.VerifyIntegrity().IsValid);
            Assert.Throws<SqliteException>(() => Execute(
                databasePath,
                "UPDATE reconciliation_cases SET local_evidence = 'rewritten' WHERE fact_sequence > 0;"));
            Assert.Throws<SqliteException>(() => Execute(
                databasePath,
                "DELETE FROM reconciliation_cases WHERE fact_sequence > 0;"));
            Assert.Equal(new[] { opened, resolved }, caseStore.Read(opened.ClientOrderId!.Value));
            Assert.True(store.VerifyIntegrity().IsValid);
        }

        using var reopened = new SqliteOrderEventStore(databasePath, Clock());
        Assert.Equal(new[] { opened, resolved }, reopened.ReadReconciliationCases(opened.ClientOrderId!.Value));
    }

    [Fact]
    public void RestoreInvalidBackup_RemovesItsNewlyReservedTarget()
    {
        using var directory = new TestDirectory();
        var unrelatedPath = directory.File("unrelated.db");
        var targetPath = directory.File("restore-target.db");
        using (var unrelated = Open(unrelatedPath, SqliteOpenMode.ReadWriteCreate))
            Execute(unrelated, "CREATE TABLE unrelated(value INTEGER NOT NULL);");

        Assert.Throws<InvalidDataException>(() =>
            SqliteOrderEventStore.RestoreDatabase(unrelatedPath, targetPath, Clock()));
        Assert.False(File.Exists(targetPath));
        Assert.False(File.Exists(targetPath + "-wal"));
        Assert.False(File.Exists(targetPath + "-shm"));
    }

    private static OrderManagementService Service(
        IOrderEventStore store,
        IEnumerable<VenueSubmitPlan>? plans = null)
    {
        var clock = Clock();
        return new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            new DeterministicSimulatedVenue(clock, plans),
            clock);
    }

    private static void Arm(OrderManagementService service, CanonicalOrderInstruction instruction)
    {
        Prepare(service, instruction);
        Assert.True(service.Arm(
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm")).IsSuccess);
    }

    private static void Prepare(OrderManagementService service, CanonicalOrderInstruction instruction)
    {
        Assert.True(service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            RiskSnapshot(instruction),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(service.Prepare(
            instruction.Identity.ClientOrderId,
            Context(instruction, "prepare")).IsSuccess);
    }

    private static CanonicalOrderInstruction ExactInstruction(string clientOrderId)
    {
        var intent = new TradeIntent(
            new InstrumentId(9001),
            TradeIntentQuantityMode.TargetPosition,
            new ScaledQuantity(20, 1),
            new ScaledPrice(950_000, 4),
            new ScaledPrice(1_100_000, 4),
            new ScaledMoney(123_450, 4),
            "sqlite-ledger-test.strategy",
            701,
            "signal-policy-exact-v1");
        var identity = new OrderIdentity(
            new IntentId($"intent-{clientOrderId}"),
            new BucketId($"bucket-{clientOrderId}"),
            new LegId($"leg-{clientOrderId}"),
            new ClientOrderId(clientOrderId),
            null,
            null,
            new CorrelationId($"correlation-{clientOrderId}"),
            new CausationId($"origin-{clientOrderId}"),
            new ExecutionLeaseId("sqlite-ledger-test-lease"),
            new FencingToken(17));
        var terms = new CanonicalOrderTerms(
            OrderSide.Buy,
            CanonicalOrderType.Market,
            CanonicalTimeInForce.Day,
            new ScaledQuantity(200, 2),
            null,
            null);
        return new CanonicalOrderInstruction(identity, intent, terms);
    }

    private static RiskInputSnapshot RiskSnapshot(CanonicalOrderInstruction instruction) =>
        new(
            instruction.TradeIntent,
            new ScaledQuantity(0, 7),
            new ScaledPrice(100_000, 3),
            new ScaledRatio(1_000, 3),
            new ScaledMoney(0, 8),
            new ScaledMoney(0, 9),
            new ScaledMoney(0, 10),
            new DateOnly(2026, 8, 5));

    private static OrderEventDraft DraftCreated(
        CanonicalOrderInstruction instruction,
        string suffix) =>
        new(
            instruction.Identity.ClientOrderId,
            OrderEventKind.DraftCreated,
            OrderLifecycleState.Draft,
            OrderEventSource.Command,
            OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}-draft"),
            OmsTestData.TimestampUtc,
            OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}-draft"),
            Instruction: instruction);

    private static OrderCommandContext Context(
        CanonicalOrderInstruction instruction,
        string suffix) =>
        new(
            OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}"),
            OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}"));

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static SqliteConnection Open(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = mode,
                Pooling = false,
                Cache = SqliteCacheMode.Private,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadWrite);
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private sealed class StoreHarness : IDisposable
    {
        private readonly IDisposable? _storeLifetime;
        private readonly TestDirectory? _directory;

        private StoreHarness(
            IOrderEventStore store,
            IDisposable? storeLifetime,
            TestDirectory? directory)
        {
            Store = store;
            _storeLifetime = storeLifetime;
            _directory = directory;
        }

        internal IOrderEventStore Store { get; }

        internal static StoreHarness Create(bool durable)
        {
            if (!durable)
                return new StoreHarness(new InMemoryOrderEventStore(), null, null);

            var directory = new TestDirectory();
            try
            {
                var store = new SqliteOrderEventStore(directory.File("contract.db"), Clock());
                return new StoreHarness(store, store, directory);
            }
            catch
            {
                directory.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _storeLifetime?.Dispose();
            _directory?.Dispose();
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        private static readonly string AllowedRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "DaxAlgo.Execution.Tests"));

        internal TestDirectory()
        {
            Root = Path.GetFullPath(Path.Combine(AllowedRoot, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        internal string File(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            var expectedPrefix = AllowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a test directory outside the allowed root.");
            if (!Directory.Exists(Root))
                return;

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly))
                System.IO.File.Delete(file);
            Directory.Delete(Root, recursive: false);
        }
    }
}
