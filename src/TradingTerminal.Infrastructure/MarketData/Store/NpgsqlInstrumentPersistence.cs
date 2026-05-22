using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// PostgreSQL persistence for the instrument registry. Opens short-lived pooled connections per
/// operation (registry mutations are infrequent), so a container restart self-heals.
/// </summary>
internal sealed class NpgsqlInstrumentPersistence : IInstrumentPersistence
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public NpgsqlInstrumentPersistence(string connectionString, ILogger logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public void EnsureSchema()
    {
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        TimescaleSchema.EnsureCreated(cn, _logger);
    }

    public IReadOnlyList<Instrument> LoadInstruments()
    {
        var list = new List<Instrument>();
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT id,canonical_symbol,asset_class,exchange,currency,tick_size,multiplier FROM instruments", cn);
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
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        using var cmd = new NpgsqlCommand("SELECT broker,broker_symbol,instrument_id FROM instrument_aliases", cn);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add(((BrokerKind)rdr.GetInt32(0), rdr.GetString(1), new InstrumentId(rdr.GetInt32(2))));
        return list;
    }

    public InstrumentId UpsertInstrument(Instrument ins)
    {
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO instruments(canonical_symbol,asset_class,exchange,currency,tick_size,multiplier)
            VALUES(@s,@ac,@ex,@cur,@ts,@mul)
            ON CONFLICT(canonical_symbol,asset_class,exchange) DO UPDATE SET currency=excluded.currency
            RETURNING id
            """, cn);
        cmd.Parameters.AddWithValue("s", ins.CanonicalSymbol);
        cmd.Parameters.AddWithValue("ac", (int)ins.AssetClass);
        cmd.Parameters.AddWithValue("ex", ins.Exchange);
        cmd.Parameters.AddWithValue("cur", ins.Currency);
        cmd.Parameters.AddWithValue("ts", ins.TickSize);
        cmd.Parameters.AddWithValue("mul", ins.Multiplier);
        return new InstrumentId(Convert.ToInt32(cmd.ExecuteScalar()));
    }

    public void UpsertAlias(InstrumentAlias alias)
    {
        using var cn = new NpgsqlConnection(_connectionString);
        cn.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO instrument_aliases(broker,broker_symbol,broker_native_id,instrument_id)
            VALUES(@b,@sym,@nid,@iid)
            ON CONFLICT(broker,broker_symbol) DO UPDATE SET
                broker_native_id=excluded.broker_native_id, instrument_id=excluded.instrument_id
            """, cn);
        cmd.Parameters.AddWithValue("b", (int)alias.Broker);
        cmd.Parameters.AddWithValue("sym", alias.BrokerSymbol);
        cmd.Parameters.AddWithValue("nid", (object?)alias.BrokerNativeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("iid", alias.InstrumentId.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() { /* no long-lived connection — pooled per operation */ }
}
