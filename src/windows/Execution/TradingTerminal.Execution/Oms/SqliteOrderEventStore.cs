using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Execution.Oms;

/// <summary>Recovery action required for a non-terminal order found when a durable ledger opens.</summary>
public enum OrderRecoveryRequirement : byte
{
    /// <summary>The persisted state requires explicit reconciliation before admissions resume.</summary>
    ReconciliationRequired = 0,

    /// <summary>A prepared order was never sent and must receive fresh authorization.</summary>
    FreshAuthorizationRequired = 1,

    /// <summary>A send began without a provable acknowledgement, so its effective state is unknown.</summary>
    OutcomeUnknown = 2,
}

/// <summary>One order surfaced from the event log during durable-store startup recovery.</summary>
/// <param name="ClientOrderId">Canonical order aggregate.</param>
/// <param name="PersistedState">Last state actually committed to the event ledger.</param>
/// <param name="EffectiveRecoveryState">Safety state the recovery coordinator must apply.</param>
/// <param name="Requirement">Required recovery action; this store never contacts a venue.</param>
/// <param name="LastSequence">Last committed aggregate sequence.</param>
public sealed record OrderRecoveryEntry(
    ClientOrderId ClientOrderId,
    OrderLifecycleState PersistedState,
    OrderLifecycleState EffectiveRecoveryState,
    OrderRecoveryRequirement Requirement,
    long LastSequence);

/// <summary>Fault categories reported by the SQLite and event-chain integrity path.</summary>
public enum SqliteOrderLedgerIntegrityFault : byte
{
    /// <summary>The SQLite file, event chains, and materialized order projections are consistent.</summary>
    None = 0,

    /// <summary>SQLite reported a physical or relational integrity failure.</summary>
    SqliteIntegrityCheckFailed = 1,

    /// <summary>An event payload was unreadable or disagreed with its normalized row envelope.</summary>
    EventPayloadInvalid = 2,

    /// <summary>An aggregate sequence, lifecycle edge, previous hash, or event hash was invalid.</summary>
    EventChainInvalid = 3,

    /// <summary>A materialized order projection was missing or differed from a pure event replay.</summary>
    ProjectionMismatch = 4,

    /// <summary>An inbox or outbox row was missing, duplicated, or disagreed with its event.</summary>
    DeliveryMetadataInvalid = 5,

    /// <summary>An immutable reconciliation-case fact was malformed or its progression was invalid.</summary>
    ReconciliationFactsInvalid = 6,
}

/// <summary>Result of a non-mutating SQLite, hash-chain, and projection integrity check.</summary>
/// <param name="Fault">Detected integrity fault.</param>
/// <param name="AggregateId">Affected aggregate when applicable.</param>
/// <param name="EventIndex">Zero-based event position when applicable.</param>
/// <param name="Detail">Short diagnostic suitable for logs.</param>
public readonly record struct SqliteOrderLedgerIntegrityResult(
    SqliteOrderLedgerIntegrityFault Fault,
    ClientOrderId? AggregateId,
    int EventIndex,
    string Detail)
{
    /// <summary>Gets whether all checks passed.</summary>
    public bool IsValid => Fault == SqliteOrderLedgerIntegrityFault.None;
}

/// <summary>
/// Durable SQLite implementation of <see cref="IOrderEventStore"/>. It owns a dedicated database,
/// keeps one serialized writer connection in WAL mode, and commits inbox deduplication, the immutable
/// event, rebuildable projections, and the outbox in one transaction. The per-aggregate SHA-256 chain
/// detects accidental or silent local corruption; it is not tamper-proof against an administrator who
/// can rewrite both rows and hashes. This type has no broker, socket, network, credential, or live-order
/// routing capability.
/// </summary>
public sealed partial class SqliteOrderEventStore :
    IOrderEventStore,
    IExecutionAdmissionGate,
    IExecutionLeaseStore,
    IReconciliationCaseStore,
    IDisposable
{
    private const int EventPayloadVersion = 1;
    private const string EventColumns = """
        aggregate_id, aggregate_sequence, event_kind, state_before, state_after, source,
        deduplication_key, occurred_at_utc_ticks, recorded_at_utc_ticks, causation_id,
        previous_event_hash, event_hash, broker_order_id, exchange_order_id,
        payload_version, event_payload_json
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private static readonly (string SelectSql, string Name)[] ProjectionDigestQueries =
    [
        ("SELECT * FROM order_intents ORDER BY client_order_id;", "order_intents"),
        ("SELECT * FROM orders ORDER BY client_order_id;", "orders"),
        ("SELECT * FROM fills ORDER BY aggregate_id, aggregate_sequence;", "fills"),
        ("SELECT * FROM fees_commissions ORDER BY aggregate_id, fill_sequence;", "fees_commissions"),
        ("SELECT * FROM position_lots ORDER BY aggregate_id, fill_sequence;", "position_lots"),
        ("SELECT * FROM risk_decisions ORDER BY aggregate_id, aggregate_sequence;", "risk_decisions"),
        ("""
         SELECT * FROM reconciliation_cases
         WHERE source_order_sequence IS NOT NULL
         ORDER BY case_id, fact_sequence;
         """, "reconciliation_cases:event-derived"),
    ];

    private readonly object _gate = new();
    private SqliteConnection _writeConnection;
    private FileStream? _writerLease;
    private readonly ReadOnlyCollection<OrderRecoveryEntry> _recoverySet;
    private bool _disposed;

    /// <summary>Creates a store at the dedicated default local-application-data path.</summary>
    /// <param name="clock">Injected UTC clock used only to timestamp applied schema migrations.</param>
    public SqliteOrderEventStore(IClock clock)
        : this(DefaultDatabasePath, clock)
    {
    }

    /// <summary>Creates or opens a dedicated order-ledger database at an injected path.</summary>
    /// <param name="databasePath">SQLite file path, or <c>:memory:</c> for a connection-scoped test store.</param>
    /// <param name="clock">Injected UTC clock used only to timestamp applied schema migrations.</param>
    public SqliteOrderEventStore(string databasePath, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(clock);

        DatabasePath = NormalizeDatabasePath(databasePath);
        if (!IsInMemory(DatabasePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            try
            {
                _writerLease = new FileStream(
                    DatabasePath + ".writer.lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    "Another durable writer already owns the configured execution ledger.",
                    exception);
            }
        }

        _writeConnection = new SqliteConnection(BuildConnectionString(DatabasePath, SqliteOpenMode.ReadWriteCreate));
        try
        {
            _writeConnection.Open();
            SchemaVersion = SqliteOrderLedgerSchema.ApplyMigrations(_writeConnection, clock);
            JournalMode = SqliteOrderLedgerSchema.ApplyConnectionPragmas(_writeConnection);
            if (!IsInMemory(DatabasePath) &&
                !string.Equals(JournalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The durable order ledger requires WAL mode; SQLite reported '{JournalMode}'.");
            }
            _recoverySet = new ReadOnlyCollection<OrderRecoveryEntry>(LoadRecoverySet());
        }
        catch
        {
            _writeConnection.Dispose();
            _writerLease?.Dispose();
            throw;
        }
    }

    /// <summary>Default ledger file, intentionally distinct from every market-data database.</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "Execution",
        "execution-ledger.db");

    /// <summary>Gets the normalized injected database path.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the applied forward-only schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the journal mode reported by SQLite after initialization.</summary>
    public string JournalMode { get; }

    /// <summary>
    /// Gets the immutable startup recovery set rebuilt from events present when this store opened.
    /// A later coordinator must resolve it before admissions; this persistence slice does not query a venue.
    /// </summary>
    public IReadOnlyList<OrderRecoveryEntry> RecoverySet => _recoverySet;

    /// <summary>Gets whether startup recovery and all durable reconciliation facts permit admission.</summary>
    public bool CanAdmitNewOrders
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _recoverySet.Count == 0 && !HasUnresolvedMaterialReconciliationCases();
            }
        }
    }

    /// <inheritdoc />
    public bool CanAdmitAfterStartupReconciliation
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return !_recoverySet.Any(RequiresFreshPredispatchRecovery) &&
                       !HasUnresolvedMaterialReconciliationCases();
            }
        }
    }

    /// <inheritdoc />
    public OrderEventAppendResult Append(OrderEventDraft draft, DateTime recordedAtUtc)
    {
        var basicFault = ValidateDraft(draft, recordedAtUtc);
        if (basicFault != OrderEventAppendFault.None)
            return Rejected(basicFault);

        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _writeConnection.BeginTransaction();

            var prior = ReadInbox(draft.Source, draft.DeduplicationKey, transaction);
            if (prior.HasValue)
            {
                if (prior.Value.Draft != draft)
                    return Rejected(OrderEventAppendFault.ConflictingDuplicate);
                var priorEvent = ReadEvent(
                    prior.Value.AggregateId,
                    prior.Value.AggregateSequence,
                    transaction);
                if (!string.Equals(prior.Value.EventHash, priorEvent.EventHash, StringComparison.Ordinal))
                    throw new InvalidDataException("The inbox event hash does not match its committed event.");
                return new OrderEventAppendResult(
                    OrderEventAppendStatus.ExactReplay,
                    OrderEventAppendFault.None,
                    priorEvent);
            }

            var stream = ReadStream(draft.AggregateId, transaction);
            var previous = stream.Count == 0 ? null : stream[^1];
            if (!OrderLifecycle.IsEventSourceAllowed(draft.Kind, draft.Source))
                return Rejected(OrderEventAppendFault.InvalidEventSource);
            if (previous is null)
            {
                if (draft.Kind != OrderEventKind.DraftCreated ||
                    draft.StateAfter != OrderLifecycleState.Draft ||
                    draft.Instruction is null ||
                    draft.Instruction.Identity.ClientOrderId != draft.AggregateId)
                {
                    return Rejected(OrderEventAppendFault.InvalidInitialEvent);
                }
            }
            else if (!OrderEventTransitionRules.CanApply(previous.StateAfter, draft.StateAfter, draft.Kind))
            {
                return Rejected(OrderEventAppendFault.IllegalTransition);
            }

            if (previous?.AggregateSequence == long.MaxValue || ReadLastOutboxSequence(transaction) == long.MaxValue)
                return Rejected(OrderEventAppendFault.SequenceExhausted);

            var aggregateSequence = previous is null ? 1 : previous.AggregateSequence + 1;
            var previousHash = previous?.EventHash ?? OrderEventHash.EmptyPreviousHash;
            var eventHash = OrderEventHash.Compute(
                draft.AggregateId,
                aggregateSequence,
                draft.Kind,
                previous?.StateAfter,
                draft.StateAfter,
                draft.Source,
                draft.DeduplicationKey,
                draft.OccurredAtUtc,
                recordedAtUtc,
                draft.CausationId,
                previousHash,
                draft.Instruction,
                draft.RiskDecision,
                draft.BrokerOrderId,
                draft.ExchangeOrderId,
                draft.Fill,
                draft.ReplacementTerms,
                draft.Reconciliation,
                draft.Reason);
            var committed = new OrderEvent(
                draft.AggregateId,
                aggregateSequence,
                draft.Kind,
                previous?.StateAfter,
                draft.StateAfter,
                draft.Source,
                draft.DeduplicationKey,
                draft.OccurredAtUtc,
                recordedAtUtc,
                draft.CausationId,
                previousHash,
                eventHash,
                draft.Instruction,
                draft.RiskDecision,
                draft.BrokerOrderId,
                draft.ExchangeOrderId,
                draft.Fill,
                draft.ReplacementTerms,
                draft.Reconciliation,
                draft.Reason);

            var candidate = new List<OrderEvent>(stream.Count + 1);
            candidate.AddRange(stream);
            candidate.Add(committed);
            var projection = OrderProjector.Rebuild(candidate);
            if (!projection.IsSuccess)
            {
                return new OrderEventAppendResult(
                    OrderEventAppendStatus.Rejected,
                    OrderEventAppendFault.ProjectionRejected,
                    null,
                    projection.Fault);
            }

            InsertEvent(committed, transaction);
            InsertInbox(draft, committed, transaction);
            RebuildAggregateProjections(candidate, projection.Projection!, transaction);
            InsertOutbox(committed, transaction);
            transaction.Commit();

            return new OrderEventAppendResult(
                OrderEventAppendStatus.Appended,
                OrderEventAppendFault.None,
                committed);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OrderEvent> Read(ClientOrderId aggregateId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var events = ReadStream(aggregateId, null);
            return events.Count == 0
                ? Array.Empty<OrderEvent>()
                : Array.AsReadOnly(events.ToArray());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OrderEventOutboxEntry> ReadOutbox(long afterExclusiveSequence = 0)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = $"""
                SELECT o.outbox_sequence, o.event_hash, {QualifyEventColumns("e")}
                FROM outbox AS o
                JOIN order_events AS e
                  ON e.aggregate_id = o.aggregate_id
                 AND e.aggregate_sequence = o.aggregate_sequence
                WHERE o.outbox_sequence > $after
                ORDER BY o.outbox_sequence;
                """;
            command.Parameters.AddWithValue("$after", afterExclusiveSequence);
            using var reader = command.ExecuteReader();
            var result = new List<OrderEventOutboxEntry>();
            while (reader.Read())
            {
                var orderEvent = DeserializeEvent(reader, 2);
                if (!string.Equals(reader.GetString(1), orderEvent.EventHash, StringComparison.Ordinal))
                    throw new InvalidDataException("An outbox event hash does not match its committed event.");
                result.Add(new OrderEventOutboxEntry(reader.GetInt64(0), orderEvent));
            }
            return result.Count == 0
                ? Array.Empty<OrderEventOutboxEntry>()
                : new ReadOnlyCollection<OrderEventOutboxEntry>(result);
        }
    }

    /// <inheritdoc />
    public bool TryAppend(ReconciliationCase reconciliationCase)
    {
        if (reconciliationCase is null || !reconciliationCase.IsValid)
            return false;

        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _writeConnection.BeginTransaction();
            var latest = ReadLatestReconciliationCase(reconciliationCase.CaseId, transaction);
            if (latest is null)
            {
                if (!InMemoryReconciliationCaseStore.CanStartFactSequence(reconciliationCase))
                    return false;
            }
            else
            {
                if (latest == reconciliationCase)
                    return true;
                if (!InMemoryReconciliationCaseStore.HasSameObservation(latest, reconciliationCase) ||
                    reconciliationCase.Status <= latest.Status)
                {
                    return false;
                }
            }

            var factSequence = ReadNextReconciliationFactSequence(reconciliationCase.CaseId, transaction);
            using var command = _writeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reconciliation_cases(
                    case_id, fact_sequence, account_adapter_id, account_id,
                    subject_kind, subject_key, client_order_id, kind, status,
                    local_evidence, broker_evidence, opened_at_utc_ticks,
                    resolved_at_utc_ticks, resolved_by, resolution_evidence)
                VALUES (
                    $caseId, $factSequence, $accountAdapterId, $accountId,
                    $subjectKind, $subjectKey, $clientOrderId, $kind, $status,
                    $localEvidence, $brokerEvidence, $openedAt,
                    $resolvedAt, $resolvedBy, $resolutionEvidence);
                """;
            AddParameter(command, "$caseId", reconciliationCase.CaseId.Value);
            AddParameter(command, "$factSequence", factSequence);
            AddParameter(command, "$accountAdapterId", reconciliationCase.Account.AdapterId.Value);
            AddParameter(command, "$accountId", reconciliationCase.Account.AccountId.Value);
            AddParameter(command, "$subjectKind", (int)reconciliationCase.SubjectKind);
            AddParameter(command, "$subjectKey", reconciliationCase.SubjectKey);
            AddParameter(command, "$clientOrderId", reconciliationCase.ClientOrderId?.Value);
            AddParameter(command, "$kind", (int)reconciliationCase.Kind);
            AddParameter(command, "$status", (int)reconciliationCase.Status);
            AddParameter(command, "$localEvidence", reconciliationCase.LocalEvidence);
            AddParameter(command, "$brokerEvidence", reconciliationCase.BrokerEvidence);
            AddParameter(command, "$openedAt", reconciliationCase.OpenedAtUtc.Ticks);
            AddParameter(command, "$resolvedAt", reconciliationCase.ResolvedAtUtc?.Ticks);
            AddParameter(command, "$resolvedBy", reconciliationCase.ResolvedBy);
            AddParameter(command, "$resolutionEvidence", reconciliationCase.ResolutionEvidence);
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    IReadOnlyList<ReconciliationCase> IReconciliationCaseStore.Read(ClientOrderId clientOrderId) =>
        ReadReconciliationCases(clientOrderId);

    IReadOnlyList<ReconciliationCase> IReconciliationCaseStore.Read(BrokerExecutionAccount account) =>
        ReadReconciliationCases(account);

    IReadOnlyList<ReconciliationCase> IReconciliationCaseStore.Read(ReconciliationCaseId caseId) =>
        ReadReconciliationCases(caseId);

    /// <summary>Reads the immutable reconciliation-case facts recorded for one order.</summary>
    public IReadOnlyList<ReconciliationCase> ReadReconciliationCases(ClientOrderId clientOrderId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = """
                SELECT case_id, account_adapter_id, account_id, subject_kind, subject_key,
                       client_order_id, kind, status, local_evidence, broker_evidence,
                       opened_at_utc_ticks, resolved_at_utc_ticks, resolved_by, resolution_evidence
                FROM reconciliation_cases
                WHERE client_order_id = $clientOrderId AND kind IS NOT NULL
                ORDER BY case_id COLLATE BINARY, fact_sequence;
                """;
            AddParameter(command, "$clientOrderId", clientOrderId.Value);
            return ReadReconciliationCases(command);
        }
    }

    /// <summary>Reads explicit reconciliation-case facts for one adapter/account.</summary>
    public IReadOnlyList<ReconciliationCase> ReadReconciliationCases(BrokerExecutionAccount account)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = """
                SELECT case_id, account_adapter_id, account_id, subject_kind, subject_key,
                       client_order_id, kind, status, local_evidence, broker_evidence,
                       opened_at_utc_ticks, resolved_at_utc_ticks, resolved_by, resolution_evidence
                FROM reconciliation_cases
                WHERE account_adapter_id = $adapterId AND account_id = $accountId AND kind IS NOT NULL
                ORDER BY case_id COLLATE BINARY, fact_sequence;
                """;
            AddParameter(command, "$adapterId", account.AdapterId.Value);
            AddParameter(command, "$accountId", account.AccountId.Value);
            return ReadReconciliationCases(command);
        }
    }

    /// <summary>Reads the complete explicit fact sequence for one case.</summary>
    public IReadOnlyList<ReconciliationCase> ReadReconciliationCases(ReconciliationCaseId caseId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = """
                SELECT case_id, account_adapter_id, account_id, subject_kind, subject_key,
                       client_order_id, kind, status, local_evidence, broker_evidence,
                       opened_at_utc_ticks, resolved_at_utc_ticks, resolved_by, resolution_evidence
                FROM reconciliation_cases
                WHERE case_id = $caseId AND kind IS NOT NULL
                ORDER BY fact_sequence;
                """;
            AddParameter(command, "$caseId", caseId.Value);
            return ReadReconciliationCases(command);
        }
    }

    /// <summary>Reads the current materialized order row without consulting the event projector.</summary>
    public OrderProjection? ReadProjection(ClientOrderId aggregateId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = """
                SELECT projection_payload_json
                FROM orders
                WHERE client_order_id = $aggregateId;
                """;
            command.Parameters.AddWithValue("$aggregateId", aggregateId.Value);
            var payload = command.ExecuteScalar() as string;
            if (payload is null)
                return null;
            var projection = Deserialize<OrderProjection>(payload, "order projection");
            if (projection.ClientOrderId != aggregateId)
                throw new InvalidDataException("The materialized order projection has the wrong aggregate identity.");
            return projection;
        }
    }

    /// <summary>
    /// Deletes and reconstructs all event-derived projections inside one transaction. The immutable
    /// event ledger, inbox, and outbox are read but never rewritten.
    /// </summary>
    /// <returns>Number of aggregate projections rebuilt.</returns>
    public int RebuildProjections()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _writeConnection.BeginTransaction();
            var streams = ReadAllStreams(transaction);
            var rebuilt = new List<(List<OrderEvent> Events, OrderProjection Projection)>(streams.Count);
            foreach (var stream in streams.Values)
            {
                var result = OrderProjector.Rebuild(stream);
                if (!result.IsSuccess)
                {
                    throw new InvalidDataException(
                        $"Cannot rebuild aggregate '{stream[0].AggregateId}': {result.Fault}/{result.ChainFault}.");
                }

                rebuilt.Add((stream, result.Projection!));
            }

            ClearEventDerivedProjections(transaction);
            foreach (var item in rebuilt)
                RebuildAggregateProjections(item.Events, item.Projection, transaction, deleteExisting: false);
            transaction.Commit();
            return rebuilt.Count;
        }
    }

    /// <summary>
    /// Runs SQLite's integrity check, verifies every per-order hash chain, and compares each persisted
    /// order projection with a pure replay. The chain's guarantee remains bounded to accidental/silent
    /// corruption and does not resist an administrator who recomputes both altered rows and hashes.
    /// </summary>
    public SqliteOrderLedgerIntegrityResult VerifyIntegrity()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var sqliteIssue = ReadSqliteIntegrityIssue(_writeConnection);
            if (sqliteIssue is not null)
            {
                return new SqliteOrderLedgerIntegrityResult(
                    SqliteOrderLedgerIntegrityFault.SqliteIntegrityCheckFailed,
                    null,
                    -1,
                    sqliteIssue);
            }

            Dictionary<ClientOrderId, List<OrderEvent>> streams;
            try
            {
                streams = ReadAllStreams(null);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or JsonException or NotSupportedException)
            {
                return new SqliteOrderLedgerIntegrityResult(
                    SqliteOrderLedgerIntegrityFault.EventPayloadInvalid,
                    null,
                    -1,
                    exception.Message);
            }

            var rebuiltStreams = new List<(List<OrderEvent> Events, OrderProjection Projection)>(streams.Count);
            foreach (var (aggregateId, stream) in streams)
            {
                var rebuilt = OrderProjector.Rebuild(stream);
                if (!rebuilt.IsSuccess)
                {
                    return new SqliteOrderLedgerIntegrityResult(
                        SqliteOrderLedgerIntegrityFault.EventChainInvalid,
                        aggregateId,
                        rebuilt.EventIndex,
                        $"{rebuilt.Fault}/{rebuilt.ChainFault}");
                }
                rebuiltStreams.Add((stream, rebuilt.Projection!));
            }

            try
            {
                var deliveryIssue = ReadDeliveryIntegrityIssue(streams.Values.Sum(static stream => stream.Count));
                if (deliveryIssue is not null)
                {
                    return new SqliteOrderLedgerIntegrityResult(
                        SqliteOrderLedgerIntegrityFault.DeliveryMetadataInvalid,
                        null,
                        -1,
                        deliveryIssue);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                return new SqliteOrderLedgerIntegrityResult(
                    SqliteOrderLedgerIntegrityFault.DeliveryMetadataInvalid,
                    null,
                    -1,
                    exception.Message);
            }

            try
            {
                var reconciliationIssue = ReadReconciliationFactIntegrityIssue();
                if (reconciliationIssue is not null)
                {
                    return new SqliteOrderLedgerIntegrityResult(
                        SqliteOrderLedgerIntegrityFault.ReconciliationFactsInvalid,
                        null,
                        -1,
                        reconciliationIssue);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or SqliteException)
            {
                return new SqliteOrderLedgerIntegrityResult(
                    SqliteOrderLedgerIntegrityFault.ReconciliationFactsInvalid,
                    null,
                    -1,
                    exception.Message);
            }

            try
            {
                using var transaction = _writeConnection.BeginTransaction();
                var persistedDigest = ComputeProjectionDigest(transaction);
                ClearEventDerivedProjections(transaction);
                foreach (var rebuilt in rebuiltStreams)
                {
                    RebuildAggregateProjections(
                        rebuilt.Events,
                        rebuilt.Projection,
                        transaction,
                        deleteExisting: false);
                }
                var replayedDigest = ComputeProjectionDigest(transaction);
                transaction.Rollback();
                if (!string.Equals(persistedDigest, replayedDigest, StringComparison.Ordinal))
                {
                    return new SqliteOrderLedgerIntegrityResult(
                        SqliteOrderLedgerIntegrityFault.ProjectionMismatch,
                        null,
                        -1,
                        "An event-derived projection differs from a pure replay.");
                }
            }
            catch (Exception exception) when (
                exception is InvalidDataException or JsonException or SqliteException)
            {
                return new SqliteOrderLedgerIntegrityResult(
                    SqliteOrderLedgerIntegrityFault.ProjectionMismatch,
                    null,
                    -1,
                    exception.Message);
            }

            return new SqliteOrderLedgerIntegrityResult(
                SqliteOrderLedgerIntegrityFault.None,
                null,
                -1,
                "ok");
        }
    }

    /// <summary>Creates a coherent SQLite backup with the writer serialized against the snapshot.</summary>
    public void BackupTo(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        lock (_gate)
        {
            ThrowIfDisposed();
            var sourceIntegrity = VerifyIntegrity();
            if (!sourceIntegrity.IsValid)
                throw new InvalidDataException($"Live ledger integrity check failed: {sourceIntegrity.Detail}");
            var destinationPath = NormalizeDatabasePath(backupPath);
            if (IsInMemory(destinationPath))
                throw new ArgumentException("A durable backup requires a file path.", nameof(backupPath));
            if (!IsInMemory(DatabasePath) &&
                string.Equals(destinationPath, DatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Backup path must differ from the live ledger path.", nameof(backupPath));
            }
            if (!IsInMemory(destinationPath) && File.Exists(destinationPath))
                throw new IOException("The backup destination already exists.");
            if (!IsInMemory(destinationPath))
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            var destinationReserved = false;
            try
            {
                ReserveNewDatabaseFile(destinationPath);
                destinationReserved = true;
                using var destination = new SqliteConnection(
                    BuildConnectionString(destinationPath, SqliteOpenMode.ReadWriteCreate));
                destination.Open();
                _writeConnection.BackupDatabase(destination);
                var issue = ReadSqliteIntegrityIssue(destination);
                if (issue is not null)
                    throw new InvalidDataException($"Backup integrity check failed: {issue}");
            }
            catch
            {
                if (destinationReserved)
                    DeleteIncompleteDatabase(destinationPath);
                throw;
            }
        }
    }

    /// <summary>
    /// Restores a validated ledger backup into a new database file using SQLite's backup API.
    /// The destination must not exist; an open writer is never replaced in place.
    /// </summary>
    public static void RestoreDatabase(string backupPath, string destinationPath, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(clock);

        var sourcePath = NormalizeDatabasePath(backupPath);
        var targetPath = NormalizeDatabasePath(destinationPath);
        if (IsInMemory(sourcePath) || IsInMemory(targetPath))
            throw new ArgumentException("Backup and restore paths must be durable database files.");
        if (!IsInMemory(sourcePath) && !File.Exists(sourcePath))
            throw new FileNotFoundException("The ledger backup does not exist.", sourcePath);
        if (!IsInMemory(targetPath) && File.Exists(targetPath))
            throw new IOException("The restore destination already exists.");
        if (!IsInMemory(sourcePath) && !IsInMemory(targetPath) &&
            string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Restore destination must differ from the backup path.", nameof(destinationPath));
        }
        if (!IsInMemory(targetPath))
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var targetReserved = false;
        try
        {
            ReserveNewDatabaseFile(targetPath);
            targetReserved = true;
            using (var source = new SqliteConnection(BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly)))
            {
                source.Open();
                if (ReadApplicationId(source) != SqliteOrderLedgerSchema.ApplicationId)
                    throw new InvalidDataException("The backup is not a DaxAlgo execution ledger.");
                var sourceIssue = ReadSqliteIntegrityIssue(source);
                if (sourceIssue is not null)
                    throw new InvalidDataException($"Backup integrity check failed: {sourceIssue}");

                using var destination = new SqliteConnection(
                    BuildConnectionString(targetPath, SqliteOpenMode.ReadWriteCreate));
                destination.Open();
                source.BackupDatabase(destination);
            }

            using var restored = new SqliteOrderEventStore(targetPath, clock);
            var restoredIntegrity = restored.VerifyIntegrity();
            if (!restoredIntegrity.IsValid)
                throw new InvalidDataException($"Restored ledger integrity check failed: {restoredIntegrity.Detail}");
        }
        catch
        {
            if (targetReserved)
                DeleteIncompleteDatabase(targetPath);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _writeConnection.Dispose();
            _writerLease?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Releases this process's durable-writer file gate and reopens the database query-only. This
    /// keeps ledger/outbox visibility for a stale service while allowing a replacement service to
    /// open the database and acquire a strictly newer fencing generation.
    /// </summary>
    internal void RelinquishWriterAccess()
    {
        lock (_gate)
        {
            if (_disposed || _writerLease is null)
                return;

            SqliteConnection? readOnlyConnection = null;
            try
            {
                readOnlyConnection = new SqliteConnection(
                    BuildConnectionString(DatabasePath, SqliteOpenMode.ReadOnly));
                readOnlyConnection.Open();
                using var command = readOnlyConnection.CreateCommand();
                command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON;";
                command.ExecuteNonQuery();

                _writeConnection.Dispose();
                _writeConnection = readOnlyConnection;
                readOnlyConnection = null;
            }
            finally
            {
                readOnlyConnection?.Dispose();
                _writerLease.Dispose();
                _writerLease = null;
            }
        }
    }

    private List<OrderRecoveryEntry> LoadRecoverySet()
    {
        var result = new List<OrderRecoveryEntry>();
        var streams = ReadAllStreams(null);
        foreach (var stream in streams.Values)
        {
            var rebuilt = OrderProjector.Rebuild(stream);
            if (!rebuilt.IsSuccess)
            {
                throw new InvalidDataException(
                    $"Cannot recover aggregate '{stream[0].AggregateId}': {rebuilt.Fault}/{rebuilt.ChainFault}.");
            }

            var projection = rebuilt.Projection!;
            if (OrderLifecycle.IsTerminal(projection.State))
                continue;

            var (effectiveState, requirement) = projection.State switch
            {
                OrderLifecycleState.Prepared => (
                    OrderLifecycleState.Prepared,
                    OrderRecoveryRequirement.FreshAuthorizationRequired),
                OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging => (
                    OrderLifecycleState.Unknown,
                    OrderRecoveryRequirement.OutcomeUnknown),
                _ => (projection.State, OrderRecoveryRequirement.ReconciliationRequired),
            };
            result.Add(new OrderRecoveryEntry(
                projection.ClientOrderId,
                projection.State,
                effectiveState,
                requirement,
                projection.LastSequence));
        }

        result.Sort(static (left, right) => string.CompareOrdinal(
            left.ClientOrderId.Value,
            right.ClientOrderId.Value));
        return result;
    }

    private ReconciliationCase? ReadLatestReconciliationCase(
        ReconciliationCaseId caseId,
        SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT case_id, account_adapter_id, account_id, subject_kind, subject_key,
                   client_order_id, kind, status, local_evidence, broker_evidence,
                   opened_at_utc_ticks, resolved_at_utc_ticks, resolved_by, resolution_evidence
            FROM reconciliation_cases
            WHERE case_id = $caseId AND kind IS NOT NULL
            ORDER BY fact_sequence DESC
            LIMIT 1;
            """;
        AddParameter(command, "$caseId", caseId.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? DeserializeReconciliationCase(reader) : null;
    }

    private static IReadOnlyList<ReconciliationCase> ReadReconciliationCases(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<ReconciliationCase>();
        while (reader.Read())
            result.Add(DeserializeReconciliationCase(reader));
        return result.Count == 0
            ? Array.Empty<ReconciliationCase>()
            : new ReadOnlyCollection<ReconciliationCase>(result);
    }

    private static ReconciliationCase DeserializeReconciliationCase(SqliteDataReader reader)
    {
        var reconciliationCase = new ReconciliationCase(
            new ReconciliationCaseId(reader.GetString(0)),
            new BrokerExecutionAccount(
                new ExecutionAdapterId(reader.GetString(1)),
                new BrokerAccountId(reader.GetString(2))),
            (ReconciliationSubjectKind)reader.GetInt32(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : new ClientOrderId(reader.GetString(5)),
            (ReconciliationCaseKind)reader.GetInt32(6),
            (ReconciliationCaseStatus)reader.GetInt32(7),
            reader.GetString(8),
            reader.GetString(9),
            new DateTime(reader.GetInt64(10), DateTimeKind.Utc),
            reader.IsDBNull(11) ? null : new DateTime(reader.GetInt64(11), DateTimeKind.Utc),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
        if (!reconciliationCase.IsValid)
        {
            throw new InvalidDataException(
                $"Reconciliation case '{reconciliationCase.CaseId}' contains an invalid fact.");
        }
        return reconciliationCase;
    }

    private InboxRecord? ReadInbox(
        OrderEventSource source,
        DeduplicationKey deduplicationKey,
        SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT aggregate_id, aggregate_sequence, draft_payload_json, event_hash
            FROM inbox_dedupe
            WHERE source = $source AND deduplication_key = $deduplicationKey;
            """;
        command.Parameters.AddWithValue("$source", (int)source);
        command.Parameters.AddWithValue("$deduplicationKey", deduplicationKey.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new InboxRecord(
            new ClientOrderId(reader.GetString(0)),
            reader.GetInt64(1),
            Deserialize<OrderEventDraft>(reader.GetString(2), "inbox draft"),
            reader.GetString(3));
    }

    private void InsertEvent(OrderEvent orderEvent, SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO order_events(
                aggregate_id, aggregate_sequence, event_kind, state_before, state_after, source,
                deduplication_key, occurred_at_utc_ticks, recorded_at_utc_ticks, causation_id,
                previous_event_hash, event_hash, broker_order_id, exchange_order_id,
                payload_version, event_payload_json)
            VALUES (
                $aggregateId, $aggregateSequence, $eventKind, $stateBefore, $stateAfter, $source,
                $deduplicationKey, $occurredAt, $recordedAt, $causationId,
                $previousHash, $eventHash, $brokerOrderId, $exchangeOrderId,
                $payloadVersion, $payload);
            """;
        AddParameter(command, "$aggregateId", orderEvent.AggregateId.Value);
        AddParameter(command, "$aggregateSequence", orderEvent.AggregateSequence);
        AddParameter(command, "$eventKind", (int)orderEvent.Kind);
        AddParameter(command, "$stateBefore", orderEvent.StateBefore.HasValue ? (int)orderEvent.StateBefore.Value : null);
        AddParameter(command, "$stateAfter", (int)orderEvent.StateAfter);
        AddParameter(command, "$source", (int)orderEvent.Source);
        AddParameter(command, "$deduplicationKey", orderEvent.DeduplicationKey.Value);
        AddParameter(command, "$occurredAt", orderEvent.OccurredAtUtc.Ticks);
        AddParameter(command, "$recordedAt", orderEvent.RecordedAtUtc.Ticks);
        AddParameter(command, "$causationId", orderEvent.CausationId.Value);
        AddParameter(command, "$previousHash", orderEvent.PreviousEventHash);
        AddParameter(command, "$eventHash", orderEvent.EventHash);
        AddParameter(command, "$brokerOrderId", orderEvent.BrokerOrderId?.Value);
        AddParameter(command, "$exchangeOrderId", orderEvent.ExchangeOrderId?.Value);
        AddParameter(command, "$payloadVersion", EventPayloadVersion);
        AddParameter(command, "$payload", Serialize(orderEvent));
        command.ExecuteNonQuery();
    }

    private void InsertInbox(OrderEventDraft draft, OrderEvent committed, SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inbox_dedupe(
                source, deduplication_key, aggregate_id, aggregate_sequence, draft_payload_json, event_hash)
            VALUES ($source, $deduplicationKey, $aggregateId, $aggregateSequence, $draft, $eventHash);
            """;
        AddParameter(command, "$source", (int)draft.Source);
        AddParameter(command, "$deduplicationKey", draft.DeduplicationKey.Value);
        AddParameter(command, "$aggregateId", committed.AggregateId.Value);
        AddParameter(command, "$aggregateSequence", committed.AggregateSequence);
        AddParameter(command, "$draft", Serialize(draft));
        AddParameter(command, "$eventHash", committed.EventHash);
        command.ExecuteNonQuery();
    }

    private void InsertOutbox(OrderEvent committed, SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbox(aggregate_id, aggregate_sequence, event_hash)
            VALUES ($aggregateId, $aggregateSequence, $eventHash);
            """;
        AddParameter(command, "$aggregateId", committed.AggregateId.Value);
        AddParameter(command, "$aggregateSequence", committed.AggregateSequence);
        AddParameter(command, "$eventHash", committed.EventHash);
        command.ExecuteNonQuery();
    }

    private List<OrderEvent> ReadStream(ClientOrderId aggregateId, SqliteTransaction? transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM order_events
            WHERE aggregate_id = $aggregateId
            ORDER BY aggregate_sequence;
            """;
        command.Parameters.AddWithValue("$aggregateId", aggregateId.Value);
        using var reader = command.ExecuteReader();
        var result = new List<OrderEvent>();
        while (reader.Read())
            result.Add(DeserializeEvent(reader));
        return result;
    }

    private OrderEvent ReadEvent(
        ClientOrderId aggregateId,
        long aggregateSequence,
        SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM order_events
            WHERE aggregate_id = $aggregateId AND aggregate_sequence = $aggregateSequence;
            """;
        command.Parameters.AddWithValue("$aggregateId", aggregateId.Value);
        command.Parameters.AddWithValue("$aggregateSequence", aggregateSequence);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("The inbox references a missing order event.");
        return DeserializeEvent(reader);
    }

    private Dictionary<ClientOrderId, List<OrderEvent>> ReadAllStreams(SqliteTransaction? transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM order_events
            ORDER BY aggregate_id COLLATE BINARY, aggregate_sequence;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<ClientOrderId, List<OrderEvent>>();
        while (reader.Read())
        {
            var orderEvent = DeserializeEvent(reader);
            if (!result.TryGetValue(orderEvent.AggregateId, out var stream))
            {
                stream = [];
                result.Add(orderEvent.AggregateId, stream);
            }
            stream.Add(orderEvent);
        }
        return result;
    }

    private static OrderEvent DeserializeEvent(SqliteDataReader reader, int offset = 0)
    {
        if (reader.GetInt32(offset + 14) != EventPayloadVersion)
            throw new InvalidDataException("The order-event payload version is unsupported.");
        var orderEvent = Deserialize<OrderEvent>(reader.GetString(offset + 15), "order event");
        var stateBefore = reader.IsDBNull(offset + 3)
            ? (OrderLifecycleState?)null
            : (OrderLifecycleState)reader.GetInt32(offset + 3);
        var brokerOrderId = reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12);
        var exchangeOrderId = reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13);
        if (!string.Equals(orderEvent.AggregateId.Value, reader.GetString(offset), StringComparison.Ordinal) ||
            orderEvent.AggregateSequence != reader.GetInt64(offset + 1) ||
            (int)orderEvent.Kind != reader.GetInt32(offset + 2) ||
            orderEvent.StateBefore != stateBefore ||
            (int)orderEvent.StateAfter != reader.GetInt32(offset + 4) ||
            (int)orderEvent.Source != reader.GetInt32(offset + 5) ||
            !string.Equals(orderEvent.DeduplicationKey.Value, reader.GetString(offset + 6), StringComparison.Ordinal) ||
            orderEvent.OccurredAtUtc.Ticks != reader.GetInt64(offset + 7) ||
            orderEvent.RecordedAtUtc.Ticks != reader.GetInt64(offset + 8) ||
            !string.Equals(orderEvent.CausationId.Value, reader.GetString(offset + 9), StringComparison.Ordinal) ||
            !string.Equals(orderEvent.PreviousEventHash, reader.GetString(offset + 10), StringComparison.Ordinal) ||
            !string.Equals(orderEvent.EventHash, reader.GetString(offset + 11), StringComparison.Ordinal) ||
            !string.Equals(orderEvent.BrokerOrderId?.Value, brokerOrderId, StringComparison.Ordinal) ||
            !string.Equals(orderEvent.ExchangeOrderId?.Value, exchangeOrderId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An order-event payload disagrees with its normalized row envelope.");
        }

        return orderEvent;
    }

    private OrderProjection? ReadProjectionCore(ClientOrderId aggregateId)
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = "SELECT projection_payload_json FROM orders WHERE client_order_id = $id;";
        command.Parameters.AddWithValue("$id", aggregateId.Value);
        var payload = command.ExecuteScalar() as string;
        return payload is null ? null : Deserialize<OrderProjection>(payload, "order projection");
    }

    private int ReadProjectionCount()
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string ComputeProjectionDigest(SqliteTransaction transaction)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (selectSql, name) in ProjectionDigestQueries)
        {
            AppendDigestValue(digest, name);
            using var command = _writeConnection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = selectSql;
            using var reader = command.ExecuteReader();
            AppendDigestValue(digest, reader.FieldCount.ToString(CultureInfo.InvariantCulture));
            for (var column = 0; column < reader.FieldCount; column++)
                AppendDigestValue(digest, reader.GetName(column));
            while (reader.Read())
            {
                AppendDigestValue(digest, "row");
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    if (reader.IsDBNull(column))
                    {
                        AppendDigestValue(digest, "null");
                        continue;
                    }

                    var value = reader.GetValue(column);
                    var canonical = value switch
                    {
                        long integer => "i:" + integer.ToString(CultureInfo.InvariantCulture),
                        int integer => "i:" + integer.ToString(CultureInfo.InvariantCulture),
                        string text => "s:" + text,
                        byte[] bytes => "b:" + Convert.ToHexString(bytes),
                        _ => "o:" + Convert.ToString(value, CultureInfo.InvariantCulture),
                    };
                    AppendDigestValue(digest, canonical);
                }
            }
        }

        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendDigestValue(IncrementalHash digest, string value)
    {
        var prefix = Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture) + ":");
        var content = Encoding.UTF8.GetBytes(value);
        digest.AppendData(prefix);
        digest.AppendData(content);
    }

    private long ReadLastOutboxSequence(SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(outbox_sequence), 0) FROM outbox;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OrderEventAppendFault ValidateDraft(OrderEventDraft? draft, DateTime recordedAtUtc)
    {
        if (draft is null)
            return OrderEventAppendFault.MissingDraft;
        if (!draft.AggregateId.IsValid)
            return OrderEventAppendFault.InvalidAggregateId;
        if (!Enum.IsDefined(draft.Kind) || !Enum.IsDefined(draft.StateAfter) || !Enum.IsDefined(draft.Source))
            return OrderEventAppendFault.InvalidClassification;
        if (!draft.DeduplicationKey.IsValid)
            return OrderEventAppendFault.InvalidDeduplicationKey;
        if (!draft.CausationId.IsValid)
            return OrderEventAppendFault.InvalidCausationId;
        if (draft.OccurredAtUtc.Kind != DateTimeKind.Utc ||
            recordedAtUtc.Kind != DateTimeKind.Utc ||
            recordedAtUtc < draft.OccurredAtUtc)
        {
            return OrderEventAppendFault.InvalidTimestamp;
        }
        return OrderEventAppendFault.None;
    }

    private static OrderEventAppendResult Rejected(OrderEventAppendFault fault) =>
        new(OrderEventAppendStatus.Rejected, fault, null);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string payload, string description) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
        throw new InvalidDataException($"The persisted {description} payload was null.");

    private static string QualifyEventColumns(string alias) => string.Join(
        ", ",
        EventColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => $"{alias}.{column}"));

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private string? ReadReconciliationFactIntegrityIssue()
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = """
            SELECT case_id, fact_sequence, account_adapter_id, account_id,
                   subject_kind, subject_key, client_order_id, kind, status,
                   local_evidence, broker_evidence, opened_at_utc_ticks,
                   resolved_at_utc_ticks, resolved_by, resolution_evidence
            FROM reconciliation_cases
            WHERE fact_sequence > 0
            ORDER BY case_id COLLATE BINARY, fact_sequence;
            """;
        using var reader = command.ExecuteReader();
        var latestByCase = new Dictionary<string, (long Sequence, ReconciliationCase Fact)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var caseId = reader.GetString(0);
            var sequence = reader.GetInt64(1);
            ReconciliationCase fact;
            try
            {
                fact = new ReconciliationCase(
                    new ReconciliationCaseId(caseId),
                    new BrokerExecutionAccount(
                        new ExecutionAdapterId(reader.GetString(2)),
                        new BrokerAccountId(reader.GetString(3))),
                    (ReconciliationSubjectKind)reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : new ClientOrderId(reader.GetString(6)),
                    (ReconciliationCaseKind)reader.GetInt32(7),
                    (ReconciliationCaseStatus)reader.GetInt32(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    new DateTime(reader.GetInt64(11), DateTimeKind.Utc),
                    reader.IsDBNull(12)
                        ? null
                        : new DateTime(reader.GetInt64(12), DateTimeKind.Utc),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14));
            }
            catch (ArgumentOutOfRangeException)
            {
                return $"Reconciliation case '{caseId}' contains an invalid UTC timestamp.";
            }

            if (!fact.IsValid)
                return $"Reconciliation case '{caseId}' contains an invalid fact.";

            if (!latestByCase.TryGetValue(caseId, out var latest))
            {
                if (sequence != 1)
                    return $"Reconciliation case '{caseId}' does not start at fact sequence 1.";
                if (!InMemoryReconciliationCaseStore.CanStartFactSequence(fact))
                    return $"Reconciliation case '{caseId}' does not start with an opening observation.";
            }
            else
            {
                if (latest.Sequence == long.MaxValue || sequence != latest.Sequence + 1)
                    return $"Reconciliation case '{caseId}' has a non-contiguous fact sequence.";
                if (!InMemoryReconciliationCaseStore.HasSameObservation(latest.Fact, fact) ||
                    fact.Status <= latest.Fact.Status)
                {
                    return $"Reconciliation case '{caseId}' has an invalid immutable-fact progression.";
                }
            }

            latestByCase[caseId] = (sequence, fact);
        }

        return null;
    }

    private bool HasUnresolvedMaterialReconciliationCases()
    {
        using var command = _writeConnection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM reconciliation_cases AS current_fact
                WHERE current_fact.fact_sequence > 0
                  AND current_fact.kind IS NOT NULL
                  AND current_fact.kind <> 0
                  AND current_fact.status <> 2
                  AND current_fact.fact_sequence = (
                      SELECT MAX(candidate.fact_sequence)
                      FROM reconciliation_cases AS candidate
                      WHERE candidate.case_id = current_fact.case_id
                        AND candidate.fact_sequence > 0)
                UNION ALL
                SELECT 1
                FROM reconciliation_cases_v1_legacy AS legacy_fact
                WHERE legacy_fact.source_order_sequence IS NULL
                  AND legacy_fact.kind IS NOT NULL
                  AND legacy_fact.kind <> 0
                  AND legacy_fact.status <> 2
                  AND legacy_fact.fact_sequence = (
                      SELECT MAX(candidate.fact_sequence)
                      FROM reconciliation_cases_v1_legacy AS candidate
                      WHERE candidate.case_id = legacy_fact.case_id
                        AND candidate.fact_sequence > 0)
                LIMIT 1
            );
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool RequiresFreshPredispatchRecovery(OrderRecoveryEntry entry) =>
        entry.PersistedState is OrderLifecycleState.Draft or
            OrderLifecycleState.Validated or
            OrderLifecycleState.Prepared or
            OrderLifecycleState.Armed;

    private string? ReadDeliveryIntegrityIssue(int expectedEventCount)
    {
        var inboxCount = 0;
        using (var command = _writeConnection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT i.source, i.deduplication_key, i.event_hash, i.draft_payload_json,
                       {QualifyEventColumns("e")}
                FROM inbox_dedupe AS i
                JOIN order_events AS e
                  ON e.aggregate_id = i.aggregate_id
                 AND e.aggregate_sequence = i.aggregate_sequence
                ORDER BY i.source, i.deduplication_key;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                inboxCount++;
                var orderEvent = DeserializeEvent(reader, 4);
                var draft = Deserialize<OrderEventDraft>(reader.GetString(3), "inbox draft");
                var committedDraft = new OrderEventDraft(
                    orderEvent.AggregateId,
                    orderEvent.Kind,
                    orderEvent.StateAfter,
                    orderEvent.Source,
                    orderEvent.DeduplicationKey,
                    orderEvent.OccurredAtUtc,
                    orderEvent.CausationId,
                    orderEvent.Instruction,
                    orderEvent.RiskDecision,
                    orderEvent.BrokerOrderId,
                    orderEvent.ExchangeOrderId,
                    orderEvent.Fill,
                    orderEvent.ReplacementTerms,
                    orderEvent.Reconciliation,
                    orderEvent.Reason);
                if (reader.GetInt32(0) != (int)orderEvent.Source ||
                    !string.Equals(reader.GetString(1), orderEvent.DeduplicationKey.Value, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), orderEvent.EventHash, StringComparison.Ordinal) ||
                    draft != committedDraft)
                {
                    return "An inbox record disagrees with its committed event.";
                }
            }
        }

        if (inboxCount != expectedEventCount)
            return "The inbox row count does not match the event ledger.";

        var outboxCount = 0;
        long expectedOutboxSequence = 1;
        using (var command = _writeConnection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.outbox_sequence, o.event_hash, e.event_hash
                FROM outbox AS o
                JOIN order_events AS e
                  ON e.aggregate_id = o.aggregate_id
                 AND e.aggregate_sequence = o.aggregate_sequence
                ORDER BY o.outbox_sequence;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                outboxCount++;
                if (reader.GetInt64(0) != expectedOutboxSequence ||
                    !string.Equals(reader.GetString(1), reader.GetString(2), StringComparison.Ordinal))
                {
                    return "An outbox record disagrees with its committed event or sequence.";
                }
                expectedOutboxSequence++;
            }
        }

        return outboxCount == expectedEventCount
            ? null
            : "The outbox row count does not match the event ledger.";
    }

    private static string? ReadSqliteIntegrityIssue(SqliteConnection connection)
    {
        var issues = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var value = reader.GetString(0);
                if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
                    issues.Add(value);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                issues.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"foreign key: table={reader.GetString(0)}, rowid={reader.GetInt64(1)}"));
            }
        }
        return issues.Count == 0 ? null : string.Join("; ", issues);
    }

    private static int ReadApplicationId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

    private static void ReserveNewDatabaseFile(string databasePath)
    {
        using var reservation = new FileStream(
            databasePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
    }

    private static void DeleteIncompleteDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath + "-shm", databasePath + "-wal", databasePath })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the copy/validation failure. A locked partial artifact can be inspected safely;
                // restore and backup never replace an existing target.
            }
        }
    }

    private static string NormalizeDatabasePath(string databasePath) =>
        IsInMemory(databasePath) ? ":memory:" : Path.GetFullPath(databasePath);

    private static bool IsInMemory(string databasePath) =>
        string.Equals(databasePath, ":memory:", StringComparison.Ordinal);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct InboxRecord(
        ClientOrderId AggregateId,
        long AggregateSequence,
        OrderEventDraft Draft,
        string EventHash);
}
