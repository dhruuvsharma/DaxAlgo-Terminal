using FluentAssertions;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI.Catalog;
using Xunit;

namespace TradingTerminal.Tests.Ui;

/// <summary>
/// Tests the portable, WPF-free strategy-catalog VM through the headless catalog seam.
/// </summary>
public sealed class StrategyCatalogViewModelTests
{
    [Fact]
    public void Loads_items_from_the_headless_catalog()
    {
        var vm = new StrategyCatalogViewModel(BacktestStrategyCatalog.All);

        vm.Count.Should().Be(BacktestStrategyCatalog.All.Count);
        vm.Count.Should().BeGreaterThan(0);
        vm.SelectedItem.Should().NotBeNull("the first strategy is auto-selected");
        vm.Items.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Id) && !string.IsNullOrWhiteSpace(i.DisplayName));
    }

    [Fact]
    public void Selecting_an_item_updates_details_and_logs()
    {
        var logged = new List<string>();
        var vm = new StrategyCatalogViewModel(BacktestStrategyCatalog.All, logged.Add);

        var target = vm.Items.Last();
        vm.SelectedItem = target;

        vm.Details.Should().Contain(target.Id).And.Contain(target.DisplayName);
        logged.Should().Contain(m => m.Contains(target.Id));
    }
}
