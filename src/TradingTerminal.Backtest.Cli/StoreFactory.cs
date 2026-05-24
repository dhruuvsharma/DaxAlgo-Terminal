using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.MarketData.Store;

namespace TradingTerminal.Backtest.Cli;

/// <summary>
/// Opens a connected <see cref="IMarketDataStore"/> and resolves a canonical
/// <see cref="InstrumentId"/> by symbol — without dragging in the WPF app's full DI graph.
/// Used by the <c>run</c> command when <c>--source store</c> is supplied.
///
/// Defaults match the WPF app: SQLite at <c>%LOCALAPPDATA%\DaxAlgoTerminal\marketdata.db</c>.
/// Override with <c>--sqlite-path &lt;path&gt;</c> or <c>--postgres-conn &lt;connstr&gt;</c>.
/// </summary>
internal static class StoreFactory
{
    public static IMarketDataStore Open(string? sqlitePath, string? postgresConn)
    {
        if (!string.IsNullOrWhiteSpace(postgresConn))
        {
            // No retention from CLI — we're not the writer process.
            return new NpgsqlMarketDataStore(postgresConn, persist: false, batchSize: 1,
                quoteRetentionDays: 0, tradeRetentionDays: 0, barRetentionDays: 0,
                NullLogger<NpgsqlMarketDataStore>.Instance);
        }

        var path = ResolveSqlitePath(sqlitePath);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"SQLite store not found at {path}. Run the WPF app first to populate it, or pass --postgres-conn.", path);
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        return new SqliteMarketDataStore(cs, persist: false, batchSize: 1,
            NullLogger<SqliteMarketDataStore>.Instance);
    }

    /// <summary>Look up the canonical id for a symbol by querying the <c>instruments</c> table directly.</summary>
    public static InstrumentId? ResolveSymbol(string? sqlitePath, string? postgresConn, string symbol)
    {
        if (!string.IsNullOrWhiteSpace(postgresConn))
        {
            using var cn = new NpgsqlConnection(postgresConn);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT id FROM instruments WHERE upper(canonical_symbol) = upper($1) LIMIT 1";
            cmd.Parameters.Add(new NpgsqlParameter { Value = symbol });
            var v = cmd.ExecuteScalar();
            return v is int id ? new InstrumentId(id) : null;
        }

        var path = ResolveSqlitePath(sqlitePath);
        if (!File.Exists(path)) return null;
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var sq = new SqliteConnection(cs);
        sq.Open();
        using var c2 = sq.CreateCommand();
        c2.CommandText = "SELECT id FROM instruments WHERE upper(canonical_symbol) = upper($s) LIMIT 1";
        c2.Parameters.AddWithValue("$s", symbol);
        var got = c2.ExecuteScalar();
        return got is long l ? new InstrumentId((int)l) : null;
    }

    private static string ResolveSqlitePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal", "marketdata.db");
    }
}
