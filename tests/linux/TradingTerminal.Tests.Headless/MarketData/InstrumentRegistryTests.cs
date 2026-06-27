using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class InstrumentRegistryTests : IDisposable
{
    private readonly string _path;
    private readonly string _cs;

    public InstrumentRegistryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mdreg_{Guid.NewGuid():N}.db");
        _cs = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
    }

    private InstrumentRegistry NewRegistry() =>
        new(new SqliteInstrumentPersistence(_cs), NullLogger<InstrumentRegistry>.Instance);

    [Fact]
    public void ResolveOrCreate_is_idempotent_per_broker_symbol()
    {
        using var reg = NewRegistry();
        var c = Contract.UsStock("AAPL");

        var a = reg.ResolveOrCreate(c, BrokerKind.Alpaca);
        var b = reg.ResolveOrCreate(c, BrokerKind.Alpaca);

        a.Should().Be(b);
        reg.Resolve(BrokerKind.Alpaca, "AAPL").Should().Be(a);
        reg.ToBrokerSymbol(a, BrokerKind.Alpaca).Should().Be("AAPL");
        reg.Get(a)!.AssetClass.Should().Be(AssetClass.Equity);
    }

    [Fact]
    public void Same_instrument_on_two_brokers_shares_one_canonical_id()
    {
        using var reg = NewRegistry();
        // Same canonical key (AAPL, Equity, NASDAQ) reached from two brokers' contracts.
        var viaAlpaca = reg.ResolveOrCreate(Contract.UsStock("AAPL"), BrokerKind.Alpaca);
        var viaIb = reg.ResolveOrCreate(new Contract("AAPL", "STK", "SMART", "USD", "NASDAQ"), BrokerKind.InteractiveBrokers);

        viaIb.Should().Be(viaAlpaca);
        reg.Resolve(BrokerKind.InteractiveBrokers, "AAPL").Should().Be(viaAlpaca);
        reg.All().Should().ContainSingle(i => i.CanonicalSymbol == "AAPL");
    }

    [Fact]
    public void Mapping_survives_reopen()
    {
        InstrumentId id;
        using (var reg = NewRegistry())
            id = reg.ResolveOrCreate(Contract.UsStock("MSFT"), BrokerKind.Alpaca);

        using var reopened = NewRegistry();
        reopened.Resolve(BrokerKind.Alpaca, "MSFT").Should().Be(id);
        reopened.Get(id)!.CanonicalSymbol.Should().Be("MSFT");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }
}
