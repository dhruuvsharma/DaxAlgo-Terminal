using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// SQLite-backed reproduction job store. Cloned in structure from <c>ArchiveManifestStore</c>: one row
/// per job, a private write connection, micros timestamps, JSON-serialized spec/result blobs (with the
/// sha256 artifact refs already inside the result), and a soft-delete retention column. Lives in its
/// own DB file — it is small, purely local, and tightly coupled to the desktop app.
///
/// <para>Holds ONLY job metadata — never untrusted paper code, never the canonical market-data store,
/// never broker credentials.</para>
/// </summary>
internal sealed class ReproJobStore : IReproJobStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;

    // Microsoft.Data.Sqlite connections are NOT thread-safe. The orchestrator drives Save/Prune from
    // Task.Run pipelines that can run concurrently (SandboxOptions.MaxConcurrent > 1), so every use of
    // _writeConnection is serialised through this lock. Reads use their own short-lived connections
    // (see Find/FindCached/List/LoadUnfinished) and are safe without the lock — SQLite handles
    // concurrent readers, and a separate read connection never touches _writeConnection's state.
    private readonly object _writeLock = new();

    public ReproJobStore(string databasePath)
    {
        // A blank DataSource gives every connection a private temp DB, so the table created here would
        // be invisible to readers. Fail loudly rather than silently lose job history.
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Repro job database path must not be blank.", nameof(databasePath));
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
        lock (_writeLock) EnsureSchema(_writeConnection);
    }

    private static void EnsureSchema(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS repro_job (
                id              TEXT PRIMARY KEY,
                cache_key       TEXT NOT NULL,
                status          TEXT NOT NULL,
                spec_json       TEXT NOT NULL,
                result_json     TEXT,
                error           TEXT,
                created_micros  INTEGER NOT NULL,
                updated_micros  INTEGER NOT NULL,
                deleted         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_repro_cache  ON repro_job(cache_key);
            CREATE INDEX IF NOT EXISTS idx_repro_status ON repro_job(status);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Save(ReproJob job)
    {
        // Serialise all writes: the orchestrator can call Save concurrently from MaxConcurrent>1
        // pipelines, and the Microsoft.Data.Sqlite write connection is not thread-safe.
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO repro_job(id, cache_key, status, spec_json, result_json, error, created_micros, updated_micros, deleted)
                VALUES($id, $ck, $st, $sj, $rj, $er, $cm, $um, 0)
                ON CONFLICT(id) DO UPDATE SET
                    status=excluded.status,
                    spec_json=excluded.spec_json,
                    result_json=excluded.result_json,
                    error=excluded.error,
                    updated_micros=excluded.updated_micros;
                """;
            cmd.Parameters.AddWithValue("$id", job.Id.ToString());
            cmd.Parameters.AddWithValue("$ck", job.Spec.CacheKey);
            cmd.Parameters.AddWithValue("$st", job.Status.ToString());
            cmd.Parameters.AddWithValue("$sj", JsonSerializer.Serialize(job.Spec, JsonOpts));
            cmd.Parameters.AddWithValue("$rj", job.Result is null ? DBNull.Value : JsonSerializer.Serialize(job.Result, JsonOpts));
            cmd.Parameters.AddWithValue("$er", (object?)job.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cm", ToMicros(job.CreatedUtc));
            cmd.Parameters.AddWithValue("$um", ToMicros(job.UpdatedUtc));
            cmd.ExecuteNonQuery();
        }
    }

    public ReproJob? Find(Guid id)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT * FROM repro_job WHERE id=$id AND deleted=0 LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? Read(rdr) : null;
    }

    public ReproJob? FindCached(string cacheKey)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM repro_job
            WHERE cache_key=$ck AND status=$st AND deleted=0
            ORDER BY updated_micros DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ck", cacheKey);
        cmd.Parameters.AddWithValue("$st", ReproStatus.Succeeded.ToString());
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? Read(rdr) : null;
    }

    public IReadOnlyList<ReproJob> LoadUnfinished()
    {
        // Anything not in a terminal state is "unfinished" and gets requeued on startup.
        var terminal = new[]
        {
            ReproStatus.Succeeded.ToString(),
            ReproStatus.Failed.ToString(),
            ReproStatus.Cancelled.ToString(),
        };
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM repro_job
            WHERE deleted=0 AND status NOT IN ($s0, $s1, $s2)
            ORDER BY created_micros ASC
            """;
        cmd.Parameters.AddWithValue("$s0", terminal[0]);
        cmd.Parameters.AddWithValue("$s1", terminal[1]);
        cmd.Parameters.AddWithValue("$s2", terminal[2]);
        var result = new List<ReproJob>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(Read(rdr));
        return result;
    }

    public IReadOnlyList<ReproJob> List(int maxRows)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT * FROM repro_job WHERE deleted=0 ORDER BY updated_micros DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", maxRows);
        var result = new List<ReproJob>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(Read(rdr));
        return result;
    }

    public int PruneOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = ToMicros(DateTime.UtcNow.AddDays(-retentionDays));
        // Soft-delete is a write on the shared connection — serialise it with every other writer.
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "UPDATE repro_job SET deleted=1 WHERE deleted=0 AND updated_micros < $cut";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private static ReproJob Read(SqliteDataReader rdr)
    {
        var specJson = rdr.GetString(rdr.GetOrdinal("spec_json"));
        var spec = JsonSerializer.Deserialize<ReproSpec>(specJson, JsonOpts)
                   ?? throw new InvalidOperationException("Corrupt repro_job.spec_json row.");
        var resultOrdinal = rdr.GetOrdinal("result_json");
        ReproResult? result = rdr.IsDBNull(resultOrdinal)
            ? null
            : JsonSerializer.Deserialize<ReproResult>(rdr.GetString(resultOrdinal), JsonOpts);
        var errorOrdinal = rdr.GetOrdinal("error");
        return new ReproJob(
            Id: Guid.Parse(rdr.GetString(rdr.GetOrdinal("id"))),
            Spec: spec,
            Status: Enum.Parse<ReproStatus>(rdr.GetString(rdr.GetOrdinal("status"))),
            CreatedUtc: FromMicros(rdr.GetInt64(rdr.GetOrdinal("created_micros"))),
            UpdatedUtc: FromMicros(rdr.GetInt64(rdr.GetOrdinal("updated_micros"))),
            Result: result,
            Error: rdr.IsDBNull(errorOrdinal) ? null : rdr.GetString(errorOrdinal));
    }

    private static long ToMicros(DateTime utc) =>
        ((utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()) - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromMicros(long micros) =>
        DateTime.UnixEpoch.AddTicks(micros * 10L);

    public void Dispose()
    {
        // Don't dispose the write connection out from under an in-flight write.
        lock (_writeLock) _writeConnection.Dispose();
    }
}
