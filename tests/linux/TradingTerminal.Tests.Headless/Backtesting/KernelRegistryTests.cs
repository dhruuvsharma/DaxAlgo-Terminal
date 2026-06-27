using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Covers kernel discovery by id, schema-driven defaults/clamping, and that a registry-built
/// kernel with schema defaults actually runs through the engine.</summary>
public sealed class KernelRegistryTests
{
    private static IStrategyKernelRegistry Registry() => new StrategyKernelRegistry(NativeKernels.All);

    [Fact]
    public void Registry_resolves_kernel_and_exposes_its_schema()
    {
        var registry = Registry();

        var descriptor = registry.Find("MEANreversion"); // id match is case-insensitive
        descriptor.Should().NotBeNull();
        descriptor!.Schema.Parameters.Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "lookback", "entryZ", "exitZ", "qty" });

        registry.Create("meanReversion").Should().BeOfType<MeanReversionKernel>();
        registry.TryCreate("nope", out _).Should().BeFalse();
    }

    [Fact]
    public void Schema_resolve_applies_defaults_and_clamps_overrides()
    {
        var schema = MeanReversionKernel.Descriptor.Schema;

        schema.Defaults().GetInt("lookback", 0).Should().Be(50);

        var resolved = schema.Resolve(new Dictionary<string, double>
        {
            ["lookback"] = 99_999, // above Max 500 → clamped
            ["entryZ"] = 0.1,      // below Min 0.5 → clamped
        });
        resolved.GetInt("lookback", 0).Should().Be(500);
        resolved.GetOr("entryZ", 0).Should().Be(0.5);
        resolved.GetOr("exitZ", -1).Should().Be(0.5); // untouched → default
    }

    [Fact]
    public async Task Registry_built_kernel_runs_with_schema_defaults()
    {
        var registry = Registry();
        var descriptor = registry.Find("meanReversion")!;

        var id = new InstrumentId(1);
        var spec = new RunSpec(
            Universe: Universe.Single(new InstrumentSpec(id, Contract.UsStock("TEST"), 0.01, 1.0)),
            Data: new DataSpec(),
            StrategyId: descriptor.Id,
            Parameters: descriptor.Schema.Resolve(new Dictionary<string, double> { ["lookback"] = 20, ["entryZ"] = 1.0 }));

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var quotes = Enumerable.Range(0, 400).Select(k =>
        {
            var mid = 100 + 5 * Math.Sin(k * 2 * Math.PI / 50.0);
            return MarketEvent.OfQuote(id, new Tick(start.AddSeconds(k), mid - 0.01, mid + 0.01, 10, 10));
        });

        var report = await new BacktestEngine(new InMemoryMarketDataFeed(quotes))
            .RunAsync(spec, descriptor.Create());

        report.Trades.Should().NotBeEmpty();
    }
}
