using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TradingTerminal.Execution.Oms;

public sealed partial class SqliteOrderEventStore
{
    /// <inheritdoc />
    public ExecutionLeaseStoreAcquireResult Acquire(
        BrokerExecutionAccount account,
        ExecutionLeaseId leaseId,
        DateTime acquiredAtUtc)
    {
        if (!account.IsValid || !leaseId.IsValid || acquiredAtUtc.Kind != DateTimeKind.Utc)
        {
            return new ExecutionLeaseStoreAcquireResult(
                ExecutionLeaseStoreFault.InvalidInput,
                null,
                "The account, lease identity, or UTC timestamp is invalid.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _writeConnection.BeginTransaction();
            if (LeaseIdentityExists(leaseId, transaction))
            {
                return new ExecutionLeaseStoreAcquireResult(
                    ExecutionLeaseStoreFault.LeaseIdentityConflict,
                    null,
                    "The execution lease identity was already used.");
            }

            var prior = ReadLatestFencingToken(account, transaction);
            if (prior == long.MaxValue)
            {
                return new ExecutionLeaseStoreAcquireResult(
                    ExecutionLeaseStoreFault.TokenExhausted,
                    null,
                    "The fencing-token space is exhausted.");
            }

            var token = new FencingToken(checked(prior + 1));
            using (var command = _writeConnection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO execution_lease_generations(
                        account_adapter_id, account_id, fencing_token,
                        execution_lease_id, acquired_at_utc_ticks)
                    VALUES (
                        $accountAdapterId, $accountId, $fencingToken,
                        $executionLeaseId, $acquiredAtUtcTicks);
                    """;
                command.Parameters.AddWithValue("$accountAdapterId", account.AdapterId.Value);
                command.Parameters.AddWithValue("$accountId", account.AccountId.Value);
                command.Parameters.AddWithValue("$fencingToken", token.Value);
                command.Parameters.AddWithValue("$executionLeaseId", leaseId.Value);
                command.Parameters.AddWithValue("$acquiredAtUtcTicks", acquiredAtUtc.Ticks);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            var generation = new ExecutionLeaseGeneration(
                new ExecutionLeaseGrant(account, leaseId, token),
                acquiredAtUtc);
            return new ExecutionLeaseStoreAcquireResult(ExecutionLeaseStoreFault.None, generation);
        }
    }

    /// <inheritdoc />
    public ExecutionLeaseStoreValidationResult Validate(in ExecutionLeaseGrant grant)
    {
        if (!grant.IsValid)
        {
            return new ExecutionLeaseStoreValidationResult(
                ExecutionLeaseStoreFault.InvalidInput,
                false,
                "The execution lease grant is invalid.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _writeConnection.CreateCommand();
            command.CommandText = """
                SELECT execution_lease_id, fencing_token
                FROM execution_lease_generations
                WHERE account_adapter_id = $accountAdapterId
                  AND account_id = $accountId
                ORDER BY fencing_token DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$accountAdapterId", grant.Account.AdapterId.Value);
            command.Parameters.AddWithValue("$accountId", grant.Account.AccountId.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new ExecutionLeaseStoreValidationResult(ExecutionLeaseStoreFault.None, false);

            var leaseId = reader.GetString(0);
            var token = reader.GetInt64(1);
            var isCurrent = string.Equals(leaseId, grant.LeaseId.Value, StringComparison.Ordinal) &&
                            token == grant.FencingToken.Value;
            return new ExecutionLeaseStoreValidationResult(ExecutionLeaseStoreFault.None, isCurrent);
        }
    }

    private bool LeaseIdentityExists(ExecutionLeaseId leaseId, SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM execution_lease_generations
                WHERE execution_lease_id = $executionLeaseId
            );
            """;
        command.Parameters.AddWithValue("$executionLeaseId", leaseId.Value);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private long ReadLatestFencingToken(
        BrokerExecutionAccount account,
        SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fencing_token
            FROM execution_lease_generations
            WHERE account_adapter_id = $accountAdapterId
              AND account_id = $accountId
            ORDER BY fencing_token DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountAdapterId", account.AdapterId.Value);
        command.Parameters.AddWithValue("$accountId", account.AccountId.Value);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? 0L
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
