using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>
/// SQLite-backed archive history. One row per uploaded period. Lives in its own database file
/// (independent of whether the main store is SQLite or Postgres) because the manifest is small,
/// purely local, and tightly coupled to the desktop app — no need to dual-back-end it.
/// </summary>
internal sealed class ArchiveManifestStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private readonly string _connectionString;
    private readonly SqliteConnection _writeConnection;

    public ArchiveManifestStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();
        EnsureSchema(_writeConnection);
    }

    private static void EnsureSchema(SqliteConnection cn)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS archive_manifest (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                period_label    TEXT NOT NULL,
                from_utc_micros INTEGER NOT NULL,
                to_utc_micros   INTEGER NOT NULL,
                transport       TEXT NOT NULL,
                target_kind     TEXT NOT NULL,
                target_chat_ref TEXT,
                parts_json      TEXT NOT NULL,
                total_sha256    TEXT NOT NULL,
                rows_quotes     INTEGER NOT NULL,
                rows_bars       INTEGER NOT NULL,
                rows_trades     INTEGER NOT NULL,
                total_bytes     INTEGER NOT NULL,
                uploaded_micros INTEGER NOT NULL,
                deleted_local   INTEGER NOT NULL DEFAULT 0,
                rows_depth      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_manifest_range
                ON archive_manifest(from_utc_micros, to_utc_micros);
            """;
        cmd.ExecuteNonQuery();

        // Migration for databases created before depth archiving: add rows_depth if missing.
        AddColumnIfMissing(cn, "archive_manifest", "rows_depth", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void AddColumnIfMissing(SqliteConnection cn, string table, string column, string decl)
    {
        using var check = cn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$c";
        check.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        using var alter = cn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
        alter.ExecuteNonQuery();
    }

    public long Insert(ArchiveManifestEntry entry)
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO archive_manifest(
                period_label, from_utc_micros, to_utc_micros, transport,
                target_kind, target_chat_ref, parts_json, total_sha256,
                rows_quotes, rows_bars, rows_trades, total_bytes,
                uploaded_micros, deleted_local, rows_depth)
            VALUES($pl, $from, $to, $tx, $tk, $tc, $pj, $sha,
                   $rq, $rb, $rt, $tb, $up, $del, $rd);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$pl", entry.PeriodLabel);
        cmd.Parameters.AddWithValue("$from", ToMicros(entry.FromUtc));
        cmd.Parameters.AddWithValue("$to", ToMicros(entry.ToUtc));
        cmd.Parameters.AddWithValue("$tx", entry.Transport);
        cmd.Parameters.AddWithValue("$tk", entry.Target.Kind);
        cmd.Parameters.AddWithValue("$tc", (object?)entry.Target.ChatRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pj", JsonSerializer.Serialize(entry.Parts, JsonOpts));
        cmd.Parameters.AddWithValue("$sha", entry.TotalSha256Hex);
        cmd.Parameters.AddWithValue("$rq", entry.RowsQuotes);
        cmd.Parameters.AddWithValue("$rb", entry.RowsBars);
        cmd.Parameters.AddWithValue("$rt", entry.RowsTrades);
        cmd.Parameters.AddWithValue("$tb", entry.TotalBytes);
        cmd.Parameters.AddWithValue("$up", ToMicros(entry.UploadedUtc));
        cmd.Parameters.AddWithValue("$del", entry.DeletedLocal ? 1 : 0);
        cmd.Parameters.AddWithValue("$rd", entry.RowsDepth);
        return (long)cmd.ExecuteScalar()!;
    }

    public void MarkLocalDeleted(long id)
    {
        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = "UPDATE archive_manifest SET deleted_local=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ArchiveManifestEntry> List(string? transport, int maxRows)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = transport is null
            ? "SELECT * FROM archive_manifest ORDER BY uploaded_micros DESC LIMIT $n"
            : "SELECT * FROM archive_manifest WHERE transport=$tx ORDER BY uploaded_micros DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", maxRows);
        if (transport is not null) cmd.Parameters.AddWithValue("$tx", transport);

        var result = new List<ArchiveManifestEntry>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(Read(rdr));
        return result;
    }

    /// <summary>Id of an archive that fully covers [from, to) for the given transport (its range
    /// spans the window), or null if none — i.e. the window still holds un-offloaded data.</summary>
    public long? FindCovering(DateTime fromUtc, DateTime toUtc, string transport)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM archive_manifest
            WHERE transport=$tx AND from_utc_micros<=$from AND to_utc_micros>=$to
            ORDER BY uploaded_micros DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$tx", transport);
        cmd.Parameters.AddWithValue("$from", ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMicros(toUtc));
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public ArchiveManifestEntry? FindOverlapping(DateTime fromUtc, DateTime toUtc)
    {
        using var cn = new SqliteConnection(_connectionString);
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM archive_manifest
            WHERE from_utc_micros=$from AND to_utc_micros=$to
            ORDER BY uploaded_micros DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$from", ToMicros(fromUtc));
        cmd.Parameters.AddWithValue("$to", ToMicros(toUtc));
        using var rdr = cmd.ExecuteReader();
        return rdr.Read() ? Read(rdr) : null;
    }

    private static ArchiveManifestEntry Read(SqliteDataReader rdr)
    {
        var parts = JsonSerializer.Deserialize<List<ArchiveBlobRef>>(rdr.GetString(rdr.GetOrdinal("parts_json")), JsonOpts)
                    ?? new List<ArchiveBlobRef>();
        return new ArchiveManifestEntry(
            Id: rdr.GetInt64(rdr.GetOrdinal("id")),
            PeriodLabel: rdr.GetString(rdr.GetOrdinal("period_label")),
            FromUtc: FromMicros(rdr.GetInt64(rdr.GetOrdinal("from_utc_micros"))),
            ToUtc: FromMicros(rdr.GetInt64(rdr.GetOrdinal("to_utc_micros"))),
            Transport: rdr.GetString(rdr.GetOrdinal("transport")),
            Target: new ArchiveTarget(
                rdr.GetString(rdr.GetOrdinal("target_kind")),
                rdr.IsDBNull(rdr.GetOrdinal("target_chat_ref")) ? null : rdr.GetString(rdr.GetOrdinal("target_chat_ref"))),
            Parts: parts,
            TotalSha256Hex: rdr.GetString(rdr.GetOrdinal("total_sha256")),
            RowsQuotes: rdr.GetInt64(rdr.GetOrdinal("rows_quotes")),
            RowsBars: rdr.GetInt64(rdr.GetOrdinal("rows_bars")),
            RowsTrades: rdr.GetInt64(rdr.GetOrdinal("rows_trades")),
            TotalBytes: rdr.GetInt64(rdr.GetOrdinal("total_bytes")),
            UploadedUtc: FromMicros(rdr.GetInt64(rdr.GetOrdinal("uploaded_micros"))),
            DeletedLocal: rdr.GetInt32(rdr.GetOrdinal("deleted_local")) != 0,
            RowsDepth: rdr.GetInt64(rdr.GetOrdinal("rows_depth")));
    }

    private static long ToMicros(DateTime utc) =>
        ((utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()) - DateTime.UnixEpoch).Ticks / 10L;

    private static DateTime FromMicros(long micros) =>
        DateTime.UnixEpoch.AddTicks(micros * 10L);

    public void Dispose() => _writeConnection.Dispose();
}
