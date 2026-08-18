using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Execution.Oms;

internal static class SqliteOrderLedgerSchema
{
    internal const int ApplicationId = 0x44415845; // DAXE
    internal const int CurrentVersion = 4;

    internal static int ApplyMigrations(SqliteConnection connection, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clock);

        var applicationId = ExecuteScalarInt32(connection, "PRAGMA application_id;");
        if (applicationId != 0 && applicationId != ApplicationId)
            throw new InvalidDataException("The configured database file belongs to another application.");

        var currentVersion = ExecuteScalarInt32(connection, "PRAGMA user_version;");
        if (currentVersion > CurrentVersion)
        {
            throw new NotSupportedException(
                $"Order-ledger schema {currentVersion} is newer than supported schema {CurrentVersion}.");
        }

        if (currentVersion == 0)
        {
            EnsureUnclaimedOrOwned(connection, applicationId);
            ApplyVersion1(connection, clock);
            currentVersion = 1;
        }

        if (currentVersion == 1)
        {
            ApplyVersion2(connection, clock);
            currentVersion = 2;
        }

        if (currentVersion == 2)
        {
            ApplyVersion3(connection, clock);
            currentVersion = 3;
        }

        if (currentVersion == 3)
        {
            ApplyVersion4(connection, clock);
            currentVersion = 4;
        }

        if (ExecuteScalarInt32(connection, "PRAGMA application_id;") != ApplicationId)
            throw new InvalidDataException("The database is not a DaxAlgo execution-ledger file.");

        return currentVersion;
    }

    internal static string ApplyConnectionPragmas(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
        }

        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=WAL;";
        return Convert.ToString(journal.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void EnsureUnclaimedOrOwned(SqliteConnection connection, int applicationId)
    {
        if (applicationId == ApplicationId)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            LIMIT 1;
            """;
        var existingTable = command.ExecuteScalar() as string;
        if (!string.IsNullOrEmpty(existingTable))
        {
            throw new InvalidDataException(
                $"The configured database already contains unrelated table '{existingTable}'. " +
                "The execution ledger must own its database file.");
        }
    }

    private static void ApplyVersion1(SqliteConnection connection, IClock clock)
    {
        var appliedAtUtc = clock.UtcNow;
        if (appliedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The injected clock must return UTC values.", nameof(clock));

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Version1Sql;
        command.ExecuteNonQuery();

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_at_utc_ticks)
                VALUES (1, 'initial durable order ledger', $appliedAt);
                """;
            migration.Parameters.AddWithValue("$appliedAt", appliedAtUtc.Ticks);
            migration.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void ApplyVersion2(SqliteConnection connection, IClock clock)
    {
        var appliedAtUtc = clock.UtcNow;
        if (appliedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The injected clock must return UTC values.", nameof(clock));

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Version2Sql;
        command.ExecuteNonQuery();

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_at_utc_ticks)
                VALUES (2, 'account reconciliation case facts', $appliedAt);
                """;
            migration.Parameters.AddWithValue("$appliedAt", appliedAtUtc.Ticks);
            migration.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version=2;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void ApplyVersion3(SqliteConnection connection, IClock clock)
    {
        var appliedAtUtc = clock.UtcNow;
        if (appliedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The injected clock must return UTC values.", nameof(clock));

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Version3Sql;
        command.ExecuteNonQuery();

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_at_utc_ticks)
                VALUES (3, 'durable execution fencing generations', $appliedAt);
                """;
            migration.Parameters.AddWithValue("$appliedAt", appliedAtUtc.Ticks);
            migration.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version=3;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void ApplyVersion4(SqliteConnection connection, IClock clock)
    {
        var appliedAtUtc = clock.UtcNow;
        if (appliedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The injected clock must return UTC values.", nameof(clock));

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Version4Sql;
        command.ExecuteNonQuery();

        using (var migration = connection.CreateCommand())
        {
            migration.Transaction = transaction;
            migration.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_at_utc_ticks)
                VALUES (4, 'trade intent entry price terms', $appliedAt);
                """;
            migration.Parameters.AddWithValue("$appliedAt", appliedAtUtc.Ticks);
            migration.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "PRAGMA user_version=4;";
            version.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static int ExecuteScalarInt32(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adds the trade intent's entry price terms. Existing rows get NULLs, which is exactly right:
    /// every order written before this migration was a market entry.
    ///
    /// <para>Version1Sql is left alone deliberately - it is the historical v1 schema, and every
    /// database including a brand-new one reaches the current shape by running the migrations in
    /// order. ALTER TABLE ADD COLUMN cannot carry the paired NULL checks the v1 table declares
    /// inline, so the writer enforces them. Ledgers created before this
    /// change were hashed under digest domain "oms-order-event-v1" and will not re-verify under
    /// "oms-order-event-v2"; that break is deliberate and loud rather than silent.</para>
    /// </summary>
    private const string Version4Sql = """
        ALTER TABLE order_intents ADD COLUMN entry_limit_coefficient INTEGER;
        ALTER TABLE order_intents ADD COLUMN entry_limit_scale INTEGER;
        ALTER TABLE order_intents ADD COLUMN entry_stop_coefficient INTEGER;
        ALTER TABLE order_intents ADD COLUMN entry_stop_scale INTEGER;
        """;

    private const string Version1Sql = """
        CREATE TABLE schema_migrations (
            version                 INTEGER PRIMARY KEY CHECK (version > 0),
            name                    TEXT NOT NULL,
            applied_at_utc_ticks    INTEGER NOT NULL
        );

        -- Slice 1 has no execution-session identity or metadata yet. The required D8 table is
        -- reserved without conflating an execution lease with a session.
        CREATE TABLE execution_sessions (
            session_id              TEXT PRIMARY KEY
        );

        CREATE TABLE order_intents (
            intent_id                           TEXT NOT NULL,
            client_order_id                     TEXT PRIMARY KEY,
            bucket_id                           TEXT,
            leg_id                              TEXT NOT NULL,
            correlation_id                      TEXT NOT NULL,
            originating_causation_id            TEXT NOT NULL,
            execution_lease_id                  TEXT NOT NULL,
            fencing_token                       INTEGER NOT NULL CHECK (fencing_token > 0),
            instrument_id                       INTEGER NOT NULL,
            quantity_mode                       INTEGER NOT NULL,
            signed_units_coefficient            INTEGER NOT NULL,
            signed_units_scale                  INTEGER NOT NULL CHECK (signed_units_scale BETWEEN 0 AND 18),
            protective_stop_coefficient         INTEGER,
            protective_stop_scale               INTEGER CHECK (protective_stop_scale BETWEEN 0 AND 18),
            profit_target_coefficient           INTEGER,
            profit_target_scale                 INTEGER CHECK (profit_target_scale BETWEEN 0 AND 18),
            estimated_cost_coefficient          INTEGER NOT NULL,
            estimated_cost_scale                INTEGER NOT NULL CHECK (estimated_cost_scale BETWEEN 0 AND 18),
            strategy_id                         TEXT NOT NULL,
            strategy_note_id                    INTEGER NOT NULL,
            policy_version                      TEXT NOT NULL,
            source_sequence                     INTEGER NOT NULL CHECK (source_sequence > 0),
            source_event_hash                   TEXT NOT NULL,
            CHECK ((protective_stop_coefficient IS NULL) = (protective_stop_scale IS NULL)),
            CHECK ((profit_target_coefficient IS NULL) = (profit_target_scale IS NULL))
        );
        CREATE INDEX ix_order_intents_intent_id ON order_intents(intent_id);

        CREATE TABLE order_events (
            aggregate_id                        TEXT NOT NULL,
            aggregate_sequence                  INTEGER NOT NULL CHECK (aggregate_sequence > 0),
            event_kind                          INTEGER NOT NULL,
            state_before                        INTEGER,
            state_after                         INTEGER NOT NULL,
            source                              INTEGER NOT NULL,
            deduplication_key                   TEXT NOT NULL,
            occurred_at_utc_ticks               INTEGER NOT NULL,
            recorded_at_utc_ticks               INTEGER NOT NULL,
            causation_id                        TEXT NOT NULL,
            previous_event_hash                 TEXT NOT NULL,
            event_hash                          TEXT NOT NULL,
            broker_order_id                     TEXT,
            exchange_order_id                   TEXT,
            payload_version                     INTEGER NOT NULL CHECK (payload_version = 1),
            event_payload_json                  TEXT NOT NULL,
            PRIMARY KEY (aggregate_id, aggregate_sequence)
        );
        CREATE INDEX ix_order_events_recorded
            ON order_events(recorded_at_utc_ticks, aggregate_id, aggregate_sequence);

        CREATE TRIGGER order_events_no_update
        BEFORE UPDATE ON order_events
        BEGIN
            SELECT RAISE(ABORT, 'order_events is append-only');
        END;

        CREATE TRIGGER order_events_no_delete
        BEFORE DELETE ON order_events
        BEGIN
            SELECT RAISE(ABORT, 'order_events is append-only');
        END;

        CREATE TABLE orders (
            client_order_id                     TEXT PRIMARY KEY REFERENCES order_intents(client_order_id),
            intent_id                           TEXT NOT NULL,
            state                               INTEGER NOT NULL,
            side                                INTEGER NOT NULL,
            order_type                          INTEGER NOT NULL,
            time_in_force                       INTEGER NOT NULL,
            quantity_coefficient                INTEGER NOT NULL,
            quantity_scale                      INTEGER NOT NULL CHECK (quantity_scale BETWEEN 0 AND 18),
            limit_price_coefficient             INTEGER,
            limit_price_scale                   INTEGER CHECK (limit_price_scale BETWEEN 0 AND 18),
            stop_price_coefficient              INTEGER,
            stop_price_scale                    INTEGER CHECK (stop_price_scale BETWEEN 0 AND 18),
            replacement_side                    INTEGER,
            replacement_order_type              INTEGER,
            replacement_time_in_force           INTEGER,
            replacement_quantity_coefficient    INTEGER,
            replacement_quantity_scale          INTEGER CHECK (replacement_quantity_scale BETWEEN 0 AND 18),
            replacement_limit_coefficient       INTEGER,
            replacement_limit_scale             INTEGER CHECK (replacement_limit_scale BETWEEN 0 AND 18),
            replacement_stop_coefficient        INTEGER,
            replacement_stop_scale              INTEGER CHECK (replacement_stop_scale BETWEEN 0 AND 18),
            broker_order_id                     TEXT,
            exchange_order_id                   TEXT,
            filled_quantity_coefficient         INTEGER NOT NULL,
            filled_quantity_scale               INTEGER NOT NULL CHECK (filled_quantity_scale BETWEEN 0 AND 18),
            total_fees_coefficient              INTEGER NOT NULL,
            total_fees_scale                    INTEGER NOT NULL CHECK (total_fees_scale BETWEEN 0 AND 18),
            last_sequence                       INTEGER NOT NULL CHECK (last_sequence > 0),
            last_event_hash                     TEXT NOT NULL,
            last_causation_id                   TEXT NOT NULL,
            projection_payload_json             TEXT NOT NULL,
            CHECK ((limit_price_coefficient IS NULL) = (limit_price_scale IS NULL)),
            CHECK ((stop_price_coefficient IS NULL) = (stop_price_scale IS NULL)),
            CHECK ((replacement_side IS NULL) = (replacement_order_type IS NULL)),
            CHECK ((replacement_side IS NULL) = (replacement_time_in_force IS NULL)),
            CHECK ((replacement_side IS NULL) = (replacement_quantity_coefficient IS NULL)),
            CHECK ((replacement_side IS NULL) = (replacement_quantity_scale IS NULL)),
            CHECK ((replacement_limit_coefficient IS NULL) = (replacement_limit_scale IS NULL)),
            CHECK ((replacement_stop_coefficient IS NULL) = (replacement_stop_scale IS NULL))
        );

        CREATE TABLE fills (
            aggregate_id                        TEXT NOT NULL,
            aggregate_sequence                  INTEGER NOT NULL,
            quantity_coefficient                INTEGER NOT NULL,
            quantity_scale                      INTEGER NOT NULL CHECK (quantity_scale BETWEEN 0 AND 18),
            price_coefficient                   INTEGER NOT NULL,
            price_scale                         INTEGER NOT NULL CHECK (price_scale BETWEEN 0 AND 18),
            fee_coefficient                     INTEGER NOT NULL,
            fee_scale                           INTEGER NOT NULL CHECK (fee_scale BETWEEN 0 AND 18),
            liquidity                           INTEGER NOT NULL,
            occurred_at_utc_ticks               INTEGER NOT NULL,
            broker_order_id                     TEXT,
            exchange_order_id                   TEXT,
            PRIMARY KEY (aggregate_id, aggregate_sequence),
            FOREIGN KEY (aggregate_id, aggregate_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence)
        );

        CREATE TABLE fees_commissions (
            aggregate_id                        TEXT NOT NULL,
            fill_sequence                       INTEGER NOT NULL,
            amount_coefficient                  INTEGER NOT NULL,
            amount_scale                        INTEGER NOT NULL CHECK (amount_scale BETWEEN 0 AND 18),
            PRIMARY KEY (aggregate_id, fill_sequence),
            FOREIGN KEY (aggregate_id, fill_sequence)
                REFERENCES fills(aggregate_id, aggregate_sequence)
        );

        -- Slice 1 has no account-level close-lot allocator. Each row is the exact fill-derived lot
        -- evidence that can be rebuilt without fabricating realized-PnL or account semantics.
        CREATE TABLE position_lots (
            aggregate_id                        TEXT NOT NULL,
            fill_sequence                       INTEGER NOT NULL,
            instrument_id                       INTEGER NOT NULL,
            side                                INTEGER NOT NULL,
            quantity_coefficient                INTEGER NOT NULL,
            quantity_scale                      INTEGER NOT NULL CHECK (quantity_scale BETWEEN 0 AND 18),
            fill_price_coefficient              INTEGER NOT NULL,
            fill_price_scale                    INTEGER NOT NULL CHECK (fill_price_scale BETWEEN 0 AND 18),
            fee_coefficient                     INTEGER NOT NULL,
            fee_scale                           INTEGER NOT NULL CHECK (fee_scale BETWEEN 0 AND 18),
            source_event_hash                   TEXT NOT NULL,
            PRIMARY KEY (aggregate_id, fill_sequence),
            FOREIGN KEY (aggregate_id, fill_sequence)
                REFERENCES fills(aggregate_id, aggregate_sequence)
        );

        CREATE TABLE risk_decisions (
            aggregate_id                            TEXT NOT NULL,
            aggregate_sequence                      INTEGER NOT NULL,
            policy_id                               TEXT NOT NULL,
            policy_version                          TEXT NOT NULL,
            policy_hash                             TEXT NOT NULL,
            outcome                                 INTEGER NOT NULL,
            reason_codes                            INTEGER NOT NULL,
            maximum_order_quantity_coefficient      INTEGER NOT NULL,
            maximum_order_quantity_scale            INTEGER NOT NULL,
            maximum_order_notional_coefficient      INTEGER NOT NULL,
            maximum_order_notional_scale            INTEGER NOT NULL,
            maximum_position_coefficient            INTEGER NOT NULL,
            maximum_position_scale                  INTEGER NOT NULL,
            maximum_gross_exposure_coefficient      INTEGER NOT NULL,
            maximum_gross_exposure_scale            INTEGER NOT NULL,
            daily_loss_limit_coefficient            INTEGER NOT NULL,
            daily_loss_limit_scale                  INTEGER NOT NULL,
            input_position_coefficient              INTEGER NOT NULL,
            input_position_scale                    INTEGER NOT NULL,
            input_reference_price_coefficient       INTEGER NOT NULL,
            input_reference_price_scale             INTEGER NOT NULL,
            input_contract_multiplier_coefficient   INTEGER NOT NULL,
            input_contract_multiplier_scale         INTEGER NOT NULL,
            input_gross_exposure_coefficient        INTEGER NOT NULL,
            input_gross_exposure_scale              INTEGER NOT NULL,
            input_realized_pnl_coefficient           INTEGER NOT NULL,
            input_realized_pnl_scale                 INTEGER NOT NULL,
            input_mark_to_market_pnl_coefficient     INTEGER NOT NULL,
            input_mark_to_market_pnl_scale           INTEGER NOT NULL,
            risk_day_number                          INTEGER NOT NULL,
            input_is_complete                        INTEGER NOT NULL CHECK (input_is_complete IN (0, 1)),
            signed_order_quantity_coefficient        INTEGER NOT NULL,
            signed_order_quantity_scale              INTEGER NOT NULL,
            order_notional_coefficient               INTEGER NOT NULL,
            order_notional_scale                     INTEGER NOT NULL,
            exposure_before_position_coefficient     INTEGER NOT NULL,
            exposure_before_position_scale           INTEGER NOT NULL,
            exposure_before_instrument_coefficient   INTEGER NOT NULL,
            exposure_before_instrument_scale         INTEGER NOT NULL,
            exposure_before_gross_coefficient        INTEGER NOT NULL,
            exposure_before_gross_scale              INTEGER NOT NULL,
            exposure_after_position_coefficient      INTEGER NOT NULL,
            exposure_after_position_scale            INTEGER NOT NULL,
            exposure_after_instrument_coefficient    INTEGER NOT NULL,
            exposure_after_instrument_scale          INTEGER NOT NULL,
            exposure_after_gross_coefficient         INTEGER NOT NULL,
            exposure_after_gross_scale               INTEGER NOT NULL,
            decision_payload_json                    TEXT NOT NULL,
            source_event_hash                        TEXT NOT NULL,
            PRIMARY KEY (aggregate_id, aggregate_sequence),
            FOREIGN KEY (aggregate_id, aggregate_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence)
        );

        CREATE TABLE reconciliation_cases (
            case_id                              TEXT NOT NULL,
            fact_sequence                        INTEGER NOT NULL CHECK (fact_sequence <> 0),
            client_order_id                      TEXT NOT NULL,
            kind                                 INTEGER,
            status                               INTEGER,
            evidence                            TEXT NOT NULL,
            opened_at_utc_ticks                  INTEGER,
            resolved_at_utc_ticks                INTEGER,
            source_order_sequence                INTEGER,
            observed_state                      INTEGER,
            source_event_hash                   TEXT,
            PRIMARY KEY (case_id, fact_sequence),
            FOREIGN KEY (client_order_id, source_order_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence),
            CHECK (
                (kind IS NOT NULL AND status IS NOT NULL AND opened_at_utc_ticks IS NOT NULL
                    AND source_order_sequence IS NULL AND observed_state IS NULL AND source_event_hash IS NULL)
                OR
                (kind IS NULL AND status IS NULL AND opened_at_utc_ticks IS NULL
                    AND resolved_at_utc_ticks IS NULL AND source_order_sequence IS NOT NULL
                    AND observed_state IS NOT NULL AND source_event_hash IS NOT NULL)
            )
        );
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

        -- Slice 1 has no standalone operator/configuration audit-event domain. Order events remain
        -- auditable in their own ledger and are not relabelled as user/device audit facts.
        CREATE TABLE audit_events (
            audit_event_id                      TEXT PRIMARY KEY
        );

        CREATE TABLE inbox_dedupe (
            source                              INTEGER NOT NULL,
            deduplication_key                   TEXT NOT NULL,
            aggregate_id                        TEXT NOT NULL,
            aggregate_sequence                  INTEGER NOT NULL,
            draft_payload_json                  TEXT NOT NULL,
            event_hash                          TEXT NOT NULL,
            PRIMARY KEY (source, deduplication_key),
            FOREIGN KEY (aggregate_id, aggregate_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence)
        );

        CREATE TABLE outbox (
            outbox_sequence                     INTEGER PRIMARY KEY AUTOINCREMENT,
            aggregate_id                        TEXT NOT NULL,
            aggregate_sequence                  INTEGER NOT NULL,
            event_hash                          TEXT NOT NULL,
            UNIQUE (aggregate_id, aggregate_sequence),
            FOREIGN KEY (aggregate_id, aggregate_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence)
        );
        """;

    private const string Version2Sql = """
        DROP INDEX ix_reconciliation_cases_order;
        DROP TRIGGER reconciliation_case_facts_no_update;
        DROP TRIGGER reconciliation_case_facts_no_delete;
        ALTER TABLE reconciliation_cases RENAME TO reconciliation_cases_v1_legacy;

        -- Pre-slice-6 explicit facts lack account ownership and separate compared evidence. Preserve
        -- them honestly as immutable legacy facts rather than fabricating account/operator values.
        CREATE TRIGGER reconciliation_case_v1_legacy_no_update
        BEFORE UPDATE ON reconciliation_cases_v1_legacy
        BEGIN
            SELECT RAISE(ABORT, 'legacy reconciliation case facts are append-only');
        END;
        CREATE TRIGGER reconciliation_case_v1_legacy_no_delete
        BEFORE DELETE ON reconciliation_cases_v1_legacy
        BEGIN
            SELECT RAISE(ABORT, 'legacy reconciliation case facts are append-only');
        END;

        CREATE TABLE reconciliation_cases (
            case_id                              TEXT NOT NULL,
            fact_sequence                        INTEGER NOT NULL CHECK (fact_sequence <> 0),
            account_adapter_id                   TEXT,
            account_id                           TEXT,
            subject_kind                         INTEGER,
            subject_key                          TEXT,
            client_order_id                      TEXT,
            kind                                 INTEGER,
            status                               INTEGER,
            local_evidence                       TEXT,
            broker_evidence                      TEXT,
            opened_at_utc_ticks                  INTEGER,
            resolved_at_utc_ticks                INTEGER,
            resolved_by                          TEXT,
            resolution_evidence                  TEXT,
            evidence                             TEXT,
            source_order_sequence                INTEGER,
            observed_state                       INTEGER,
            source_event_hash                    TEXT,
            PRIMARY KEY (case_id, fact_sequence),
            FOREIGN KEY (client_order_id, source_order_sequence)
                REFERENCES order_events(aggregate_id, aggregate_sequence),
            CHECK (
                (fact_sequence > 0
                    AND account_adapter_id IS NOT NULL AND account_id IS NOT NULL
                    AND subject_kind IS NOT NULL AND subject_key IS NOT NULL
                    AND kind IS NOT NULL AND status IS NOT NULL
                    AND local_evidence IS NOT NULL AND broker_evidence IS NOT NULL
                    AND opened_at_utc_ticks IS NOT NULL AND evidence IS NULL
                    AND source_order_sequence IS NULL AND observed_state IS NULL AND source_event_hash IS NULL
                    AND ((subject_kind = 0 AND client_order_id IS NOT NULL)
                         OR (subject_kind <> 0 AND client_order_id IS NULL)))
                OR
                (fact_sequence < 0
                    AND account_adapter_id IS NULL AND account_id IS NULL
                    AND subject_kind IS NULL AND subject_key IS NULL
                    AND kind IS NULL AND status IS NULL
                    AND local_evidence IS NULL AND broker_evidence IS NULL
                    AND opened_at_utc_ticks IS NULL AND resolved_at_utc_ticks IS NULL
                    AND resolved_by IS NULL AND resolution_evidence IS NULL
                    AND client_order_id IS NOT NULL AND evidence IS NOT NULL
                    AND source_order_sequence IS NOT NULL
                    AND observed_state IS NOT NULL AND source_event_hash IS NOT NULL)
            )
        );
        CREATE INDEX ix_reconciliation_cases_order
            ON reconciliation_cases(client_order_id, case_id, fact_sequence);
        CREATE INDEX ix_reconciliation_cases_account
            ON reconciliation_cases(account_adapter_id, account_id, case_id, fact_sequence);
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

        INSERT INTO reconciliation_cases(
            case_id, fact_sequence, client_order_id, evidence,
            source_order_sequence, observed_state, source_event_hash)
        SELECT case_id, fact_sequence, client_order_id, evidence,
               source_order_sequence, observed_state, source_event_hash
        FROM reconciliation_cases_v1_legacy
        WHERE source_order_sequence IS NOT NULL;
        """;

    private const string Version3Sql = """
        CREATE TABLE execution_lease_generations (
            account_adapter_id                  TEXT NOT NULL,
            account_id                          TEXT NOT NULL,
            fencing_token                       INTEGER NOT NULL CHECK (fencing_token > 0),
            execution_lease_id                  TEXT NOT NULL,
            acquired_at_utc_ticks               INTEGER NOT NULL,
            PRIMARY KEY (account_adapter_id, account_id, fencing_token),
            UNIQUE (execution_lease_id)
        );
        CREATE INDEX ix_execution_lease_generations_latest
            ON execution_lease_generations(account_adapter_id, account_id, fencing_token DESC);
        CREATE TRIGGER execution_lease_generations_no_update
        BEFORE UPDATE ON execution_lease_generations
        BEGIN
            SELECT RAISE(ABORT, 'execution lease generations are append-only');
        END;
        CREATE TRIGGER execution_lease_generations_no_delete
        BEFORE DELETE ON execution_lease_generations
        BEGIN
            SELECT RAISE(ABORT, 'execution lease generations are append-only');
        END;
        """;
}
