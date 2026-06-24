using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Engine smoke test for the Phase-3 bridge endpoint: a canned <see cref="ReproSignalManifest"/>
/// (buy-then-sell) replayed by <see cref="ReproducedSignalStrategyKernel"/> through the real
/// <see cref="BacktestEngine"/> over a synthetic quote series must produce trades and a report. Also
/// covers the loud capability check and the factory's provenance/data-requirement surface. Fully
/// offline — no Docker, no sidecar (models KernelRegistryTests).
/// </summary>
public sealed class ReproducedSignalKernelTests
{
    private static readonly InstrumentId Id = new(1);
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ReproSignalManifest Manifest(params ReproducedSignal[] signals) =>
        new(new PaperRef("2507.22712", "Test Paper", "https://arxiv.org/abs/2507.22712"),
            "abc123def456", new EnvHash("env-hash-abc"), signals);

    private static ReproducedSignal Signal(int second, double value, InstrumentId? id = null) =>
        new(id ?? Id, Start.AddSeconds(second), value, "2507.22712", "abc123def456", new EnvHash("env-hash-abc"));

    private static RunSpec Spec() => new(
        Universe: Universe.Single(new InstrumentSpec(Id, Contract.UsStock("TEST"), 0.01, 1.0)),
        Data: new DataSpec(),
        StrategyId: ReproducedStrategyFactory.IdPrefix + "test");

    private static InMemoryMarketDataFeed Quotes()
    {
        // A flat, liquid quote stream so the replay's market orders fill cleanly.
        var quotes = Enumerable.Range(0, 60).Select(k =>
            MarketEvent.OfQuote(Id, new Tick(Start.AddSeconds(k), 100 - 0.01, 100 + 0.01, 100, 100)));
        return new InMemoryMarketDataFeed(quotes);
    }

    [Fact]
    public async Task Buy_then_sell_manifest_produces_trades_and_a_report()
    {
        // +1 at t=5 (go long), -1 at t=30 (flip short). Plus an OnEnd flatten.
        var manifest = Manifest(Signal(5, +1.0), Signal(30, -1.0));
        var kernel = new ReproducedSignalStrategyKernel(manifest);

        var report = await new BacktestEngine(Quotes()).RunAsync(Spec(), kernel);

        report.Should().NotBeNull();
        report.Trades.Should().NotBeEmpty("the replayed signals must open and close positions");
    }

    [Fact]
    public async Task Kernel_fails_loudly_when_a_signal_targets_an_instrument_outside_the_universe()
    {
        var foreign = new InstrumentId(999);
        var manifest = Manifest(Signal(5, +1.0, foreign)); // not in the single-instrument universe

        var kernel = new ReproducedSignalStrategyKernel(manifest);

        var run = async () => await new BacktestEngine(Quotes()).RunAsync(Spec(), kernel);
        await run.Should().ThrowAsync<NotSupportedException>(
            "replay must refuse signals the engine feed can't supply, mirroring the trade-tape check");
    }

    [Fact]
    public void Factory_carries_provenance_and_L1_Bars_data_requirement()
    {
        var manifest = Manifest(Signal(5, +1.0));

        var descriptor = ReproducedStrategyFactory.ToKernelDescriptor(manifest);
        descriptor.Id.Should().StartWith(ReproducedStrategyFactory.IdPrefix);
        descriptor.ResearchPaperUrl.Should().Be("https://arxiv.org/abs/2507.22712");
        descriptor.Create().Should().BeOfType<ReproducedSignalStrategyKernel>();

        var option = ReproducedStrategyFactory.ToBacktestOption(manifest);
        option.ResearchPaperUrl.Should().Be("https://arxiv.org/abs/2507.22712");
        option.DataRequirement.Should().Be(
            Core.Strategies.StrategyDataRequirement.L1 | Core.Strategies.StrategyDataRequirement.Bars);
        option.Create(Contract.UsStock("TEST")).Should().BeOfType<ReproducedSignalBacktestStrategy>();
    }
}
