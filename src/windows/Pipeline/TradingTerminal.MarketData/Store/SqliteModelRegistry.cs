using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Ml;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// SQLite-backed trained-model registry. Cloned in structure from <c>ReproJobStore</c> /
/// <c>ArchiveManifestStore</c>: one row per stored model, a private write connection guarded by a
/// lock, short-lived read connections, micros timestamps, a JSON-serialized <see cref="ModelArtifact"/>
/// blob with denormalized key/metric columns for cheap listing, a sha256 content digest per row, and
/// a soft-delete retention column. Lives in its own small DB file (default <c>ml-models.db</c>),
/// independent of the market-data backend.
///
/// <para>Holds ONLY model weights + metadata — never market data, never credentials. Successive
/// checkpoints of the same <see cref="ModelKey"/> are auto-versioned monotonically.</para>
/// </summary>
internal sealed class SqliteModelRegistry : IModelRegistry, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;

    // Microsoft.Data.Sqlite connections are not thread-safe. A window can save on close while another
    // resolves the same singleton; every write goes through this lock. Reads use their own short-lived
    // connections (SQLite handles concurrent readers).
    private readonly object _writeLock = new();

    public SqliteModelRegistry(string databasePath)
    {
        // A blank DataSource gives every connection a private temp DB, so the table created here would
        // be invisible to readers. Fail loudly rather than silently lose model history.
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Model registry database path must not be blank.", nameof(databasePath));
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
            CREATE TABLE IF NOT EXISTS model (
                model_id        TEXT PRIMARY KEY,
                model_kind      TEXT NOT NULL,
                instrument_key  TEXT NOT NULL,
                timeframe       TEXT NOT NULL,
                algorithm       TEXT NOT NULL,
                feature_hash    TEXT NOT NULL,
                version         INTEGER NOT NULL,
                samples         INTEGER NOT NULL,
                ml_mae          REAL,
                ml_hit          REAL,
                base_mae        REAL,
                base_hit        REAL,
                scored          INTEGER NOT NULL DEFAULT 0,
                sha256          TEXT NOT NULL,
                payload_json    TEXT NOT NULL,
                created_micros  INTEGER NOT NULL,
                deleted         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_model_key     ON model(model_kind, instrument_key, timeframe, algorithm, version);
            CREATE INDEX IF NOT EXISTS idx_model_created ON model(created_micros);
            """;
        cmd.ExecuteNonQuery();
    }

    public StoredModel Save(ModelArtifact artifact)
    {
        var payload = JsonSerializer.Serialize(artifact, ModelArtifactJson.Options);
        var sha = Sha256Hex(payload);
        var createdUtc = artifact.CreatedUtc == default ? DateTime.UtcNow : artifact.CreatedUtc;
        var modelId = Guid.NewGuid().ToString("N");
        var key = artifact.Key;

        lock (_writeLock)
        {
            var version = NextVersion(key);
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO model(model_id, model_kind, instrument_key, timeframe, algorithm, feature_hash,
                                  version, samples, ml_mae, ml_hit, base_mae, base_hit, scored, sha256,
                                  payload_json, created_micros, deleted)
                VALUES($id, $mk, $ik, $tf, $al, $fh, $ver, $smp, $mae, $hit, $bmae, $bhit, $scored, $sha,
                       $pj, $cm, 0);
                """;
            cmd.Parameters.AddWithValue("$id", modelId);
            cmd.Parameters.AddWithValue("$mk", key.ModelKind);
            cmd.Parameters.AddWithValue("$ik", key.InstrumentKey);
            cmd.Parameters.AddWithValue("$tf", key.Timeframe);
            cmd.Parameters.AddWithValue("$al", key.Algorithm);
            cmd.Parameters.AddWithValue("$fh", artifact.Features.ComputeHash());
            cmd.Parameters.AddWithValue("$ver", version);
            cmd.Parameters.AddWithValue("$smp", artifact.SamplesTrained);
            cmd.Parameters.AddWithValue("$mae", Real(artifact.Metrics.MlMaeTicks));
            cmd.Parameters.AddWithValue("$hit", Real(artifact.Metrics.MlHitRate));
            cmd.Parameters.AddWithValue("$bmae", Real(artifact.Metrics.BaselineMaeTicks));
            cmd.Parameters.AddWithValue("$bhit", Real(artifact.Metrics.BaselineHitRate));
            cmd.Parameters.AddWithValue("$scored", artifact.Metrics.ScoredCount);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$pj", payload);
            cmd.Parameters.AddWithValue("$cm", ToMicros(createdUtc));
            cmd.ExecuteNonQuery();
            return new StoredModel(modelId, version, sha, createdUtc);
        }
    }

    /// <summary>Next monotonic version for a key — computed over all rows (incl. soft-deleted) so a
    /// prune can never cause a version to be reused. Called under the write lock.</summary>
    private int NextVersion(ModelKey key)
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(version), 0) FROM model
            WHERE model_kind=$mk AND instrument_key=$ik AND timeframe=$tf AND algorithm=$al
            """;
        cmd.Parameters.AddWithValue("$mk", key.ModelKind);
        cmd.Parameters.AddWithValue("$ik", key.InstrumentKey);
        cmd.Parameters.AddWithValue("$tf", key.Timeframe);
        cmd.Parameters.AddWithValue("$al", key.Algorithm);
        return Convert.ToInt32(cmd.ExecuteScalar()) + 1;
    }

    public ModelArtifact? Load(string modelId)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM model WHERE model_id=$id AND deleted=0 LIMIT 1";
        cmd.Parameters.AddWithValue("$id", modelId);
        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : Deserialize(payload);
    }

    public ModelArtifact? LoadLatest(ModelKey key)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT payload_json FROM model
            WHERE model_kind=$mk AND instrument_key=$ik AND timeframe=$tf AND algorithm=$al AND deleted=0
            ORDER BY version DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$mk", key.ModelKind);
        cmd.Parameters.AddWithValue("$ik", key.InstrumentKey);
        cmd.Parameters.AddWithValue("$tf", key.Timeframe);
        cmd.Parameters.AddWithValue("$al", key.Algorithm);
        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : Deserialize(payload);
    }

    public IReadOnlyList<StoredModelInfo> List(ModelKey? filter, int maxRows)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        var sql = new StringBuilder("""
            SELECT model_id, model_kind, instrument_key, timeframe, algorithm, version, samples,
                   ml_mae, ml_hit, base_mae, base_hit, scored, sha256, created_micros
            FROM model WHERE deleted=0
            """);
        if (filter is not null)
        {
            AppendFilter(sql, cmd, "model_kind", "$mk", filter.ModelKind);
            AppendFilter(sql, cmd, "instrument_key", "$ik", filter.InstrumentKey);
            AppendFilter(sql, cmd, "timeframe", "$tf", filter.Timeframe);
            AppendFilter(sql, cmd, "algorithm", "$al", filter.Algorithm);
        }
        sql.Append(" ORDER BY created_micros DESC LIMIT $n");
        cmd.Parameters.AddWithValue("$n", maxRows);
        cmd.CommandText = sql.ToString();

        var result = new List<StoredModelInfo>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(ReadInfo(rdr));
        return result;
    }

    public bool Delete(string modelId)
    {
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "UPDATE model SET deleted=1 WHERE model_id=$id AND deleted=0";
            cmd.Parameters.AddWithValue("$id", modelId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int PruneOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = ToMicros(DateTime.UtcNow.AddDays(-retentionDays));
        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "UPDATE model SET deleted=1 WHERE deleted=0 AND created_micros < $cut";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private static void AppendFilter(StringBuilder sql, SqliteCommand cmd, string column, string param, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sql.Append(" AND ").Append(column).Append('=').Append(param);
        cmd.Parameters.AddWithValue(param, value);
    }

    private static StoredModelInfo ReadInfo(SqliteDataReader rdr)
    {
        var key = new ModelKey(
            rdr.GetString(1), rdr.GetString(2), rdr.GetString(3), rdr.GetString(4));
        var metrics = new ModelMetrics(
            ReadReal(rdr, 7), ReadReal(rdr, 8), ReadReal(rdr, 9), ReadReal(rdr, 10), rdr.GetInt64(11));
        return new StoredModelInfo(
            ModelId: rdr.GetString(0),
            Key: key,
            Version: rdr.GetInt32(5),
            SamplesTrained: rdr.GetInt64(6),
            Metrics: metrics,
            Sha256: rdr.GetString(12),
            CreatedUtc: FromMicros(rdr.GetInt64(13)));
    }

    private static ModelArtifact Deserialize(string payload) =>
        JsonSerializer.Deserialize<ModelArtifact>(payload, ModelArtifactJson.Options)
        ?? throw new InvalidOperationException("Corrupt model.payload_json row.");

    private static string Sha256Hex(string payload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // SQLite has no NaN; store non-finite metrics as NULL and read them back as NaN.
    private static object Real(double value) => double.IsFinite(value) ? value : DBNull.Value;

    private static double ReadReal(SqliteDataReader rdr, int ordinal) =>
        rdr.IsDBNull(ordinal) ? double.NaN : rdr.GetDouble(ordinal);

    private static long ToMicros(DateTime utc) =>
        ((utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()) - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromMicros(long micros) =>
        DateTime.UnixEpoch.AddTicks(micros * 10L);

    public void Dispose()
    {
        lock (_writeLock) _writeConnection.Dispose();
    }
}
