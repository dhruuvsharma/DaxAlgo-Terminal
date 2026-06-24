using Microsoft.Data.Sqlite;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>SQLite persistence for the instrument registry. Holds one long-lived connection;
/// mutations are infrequent and serialized by <see cref="InstrumentRegistry"/>.</summary>
internal sealed class SqliteInstrumentPersistence : IInstrumentPersistence
{
    private readonly SqliteConnection _connection;

    public SqliteInstrumentPersistence(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        SqliteSchema.ApplyPragmas(_connection);
    }

    public void EnsureSchema() => SqliteSchema.EnsureCreated(_connection);

    public IReadOnlyList<Instrument> LoadInstruments()
    {
        var list = new List<Instrument>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id,canonical_symbol,asset_class,exchange,currency,tick_size,multiplier FROM instruments";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(new Instrument(new InstrumentId(rdr.GetInt32(0)), rdr.GetString(1),
                (AssetClass)rdr.GetInt32(2), rdr.GetString(3), rdr.GetString(4),
                rdr.GetDouble(5), rdr.GetDouble(6)));
        return list;
    }

    public IReadOnlyList<(BrokerKind Broker, string Symbol, InstrumentId Id)> LoadAliases()
    {
        var list = new List<(BrokerKind, string, InstrumentId)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT broker,broker_symbol,instrument_id FROM instrument_aliases";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(((BrokerKind)rdr.GetInt32(0), rdr.GetString(1), new InstrumentId(rdr.GetInt32(2))));
        return list;
    }

    public InstrumentId UpsertInstrument(Instrument ins)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO instruments(canonical_symbol,asset_class,exchange,currency,tick_size,multiplier)
            VALUES($s,$ac,$ex,$cur,$ts,$mul)
            ON CONFLICT(canonical_symbol,asset_class,exchange) DO UPDATE SET currency=excluded.currency
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$s", ins.CanonicalSymbol);
        cmd.Parameters.AddWithValue("$ac", (int)ins.AssetClass);
        cmd.Parameters.AddWithValue("$ex", ins.Exchange);
        cmd.Parameters.AddWithValue("$cur", ins.Currency);
        cmd.Parameters.AddWithValue("$ts", ins.TickSize);
        cmd.Parameters.AddWithValue("$mul", ins.Multiplier);
        return new InstrumentId(Convert.ToInt32(cmd.ExecuteScalar()));
    }

    public void UpsertAlias(InstrumentAlias alias)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO instrument_aliases(broker,broker_symbol,broker_native_id,instrument_id)
            VALUES($b,$sym,$nid,$iid)
            ON CONFLICT(broker,broker_symbol) DO UPDATE SET
                broker_native_id=excluded.broker_native_id, instrument_id=excluded.instrument_id
            """;
        cmd.Parameters.AddWithValue("$b", (int)alias.Broker);
        cmd.Parameters.AddWithValue("$sym", alias.BrokerSymbol);
        cmd.Parameters.AddWithValue("$nid", (object?)alias.BrokerNativeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iid", alias.InstrumentId.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
