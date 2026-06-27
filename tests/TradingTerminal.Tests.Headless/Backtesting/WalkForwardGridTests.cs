using System;
using System.Linq;
using FluentAssertions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>
/// Covers the pluggable walk-forward grid seam: grids live on each strategy's
/// <see cref="BacktestStrategyOption.WalkForwardGrid"/> (catalog entry or plugin registration) and
/// <see cref="WalkForwardGridBuilders.For"/> resolves them — no hardcoded per-strategy switch and no
/// engine references in the resolver. This is what lets a strategy (e.g. OU once it becomes a plugin)
/// ship its walk-forward grid with itself.
/// </summary>
public sealed class WalkForwardGridTests
{
    private static BacktestStrategyOption Option(string id) =>
        BacktestStrategyCatalog.All.First(o => o.Id == id);

    [Fact]
    public void Ou_option_declares_a_walk_forward_grid() =>
        Option("ornsteinUhlenbeck").WalkForwardGrid.Should().NotBeNull();

    [Fact]
    public void Ou_grid_uses_its_own_defaults_when_axes_are_empty()
    {
        var grid = WalkForwardGridBuilders.For(Option("ornsteinUhlenbeck"), WalkForwardAxes.Defaults);

        grid.Should().HaveCount(9); // 3 default lookbacks x 3 default entryZ
        grid.Should().OnlyContain(c => c.Label.StartsWith("ou-lk"));
        grid[0].Builder(Contract.UsStock("TEST")).Should().BeOfType<OrnsteinUhlenbeckStrategy>();
    }

    [Fact]
    public void Caller_supplied_axes_override_the_grid_defaults()
    {
        var axes = WalkForwardAxes.Defaults with { Lookbacks = [100], EntryZ = [2.0] };

        var grid = WalkForwardGridBuilders.For(Option("ornsteinUhlenbeck"), axes);

        grid.Should().ContainSingle();
        grid[0].Label.Should().Be("ou-lk100-z2");
    }

    [Fact]
    public void Every_builder_is_a_fresh_instance()
    {
        var grid = WalkForwardGridBuilders.For(Option("meanReversion"), WalkForwardAxes.Defaults);
        var c = Contract.UsStock("TEST");

        grid[0].Builder(c).Should().NotBeSameAs(grid[0].Builder(c));
    }

    [Fact]
    public void Strategy_without_a_grid_has_a_null_factory_and_For_throws()
    {
        Option("buyAndHold").WalkForwardGrid.Should().BeNull();

        var act = () => WalkForwardGridBuilders.For(Option("buyAndHold"), WalkForwardAxes.Defaults);
        act.Should().Throw<ArgumentException>();
    }
}
