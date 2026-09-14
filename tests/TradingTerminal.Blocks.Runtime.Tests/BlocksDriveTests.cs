using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>The free checks a unit meets before any critic is paid to look at it.</summary>
public sealed class BlocksDriveTests
{
    [Fact]
    public async Task The_compiled_arbitrage_passes_the_drive_trading_both_legs()
    {
        var compiled = new BlocksUnitCompiler().Compile("arb", [new StrategyFile("A.cs", BlocksUnitCompilerTests.ArbitrageSource)]);
        compiled.Success.Should().BeTrue();

        var report = await BlocksDrive.RunAsync(compiled.Factory!, new DriveOptions(HasPage: true));

        report.Findings.Should().NotContain(f => f.Severity == DriveSeverity.Failure, string.Join("\n", report.Findings));
        report.Passed.Should().BeTrue();
        report.UsesOrders.Should().BeTrue();
        report.Subscribed.Should().BeEquivalentTo([new InstrumentId(1), new InstrumentId(2)]);
        report.Fills.Should().BeGreaterThan(0, "the synthetic path opens the spread past its entry");
        report.PagePosts.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_handler_that_throws_on_the_awkward_path_fails_with_its_message()
    {
        var unit = () => new LambdaUnit(
            new UnitInfo("Divides", Settings: [StrategyParameter.Instrument("i", "Instrument", new InstrumentId(3))]),
            context =>
            {
                var i = context.Settings.Instrument("i");
                context.Market.OnBar(i, BarSize.OneMinute, bar =>
                {
                    // Indexing a window that is not full yet: the classic first-minute crash.
                    var bars = context.Market.RecentBars(i, BarSize.OneMinute, 50);
                    _ = bars[49].Close;
                });
                return Task.CompletedTask;
            });

        var report = await BlocksDrive.RunAsync(unit, new DriveOptions(Steps: 40));

        report.Passed.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Code == "handler.threw" && f.Message.Contains("ArgumentOutOfRange"));
    }

    [Fact]
    public async Task A_page_the_unit_never_talks_to_fails()
    {
        var unit = () => new LambdaUnit(new UnitInfo("Silent"), _ => Task.CompletedTask);

        var report = await BlocksDrive.RunAsync(unit, new DriveOptions(Steps: 10, HasPage: true));

        report.Passed.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Code == "ui.never-sent");
    }

    [Fact]
    public async Task A_declared_setting_nothing_reads_is_reported()
    {
        var unit = () => new LambdaUnit(
            new UnitInfo("Unread", Settings:
            [
                StrategyParameter.Instrument("i", "Instrument", new InstrumentId(4)),
                StrategyParameter.Int("unused", "Does nothing", 10),
            ]),
            context =>
            {
                context.Market.OnQuote(context.Settings.Instrument("i"), _ => { });
                return Task.CompletedTask;
            });

        var report = await BlocksDrive.RunAsync(unit, new DriveOptions(Steps: 10));

        report.Passed.Should().BeTrue("an unread setting is a warning, not a broken unit");
        report.Findings.Should().ContainSingle(f => f.Code == "settings.unread" && f.Message.Contains("'unused'"));
    }

    [Fact]
    public async Task A_unit_that_throws_in_start_fails_without_hanging()
    {
        var unit = () => new LambdaUnit(new UnitInfo("Broken"), _ => throw new InvalidOperationException("no"));

        var report = await BlocksDrive.RunAsync(unit, new DriveOptions(Steps: 10));

        report.Passed.Should().BeFalse();
        report.Findings.Should().Contain(f => f.Code == "start.threw" && f.Message.Contains("no"));
    }
}
