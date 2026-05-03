using System.Reactive.Subjects;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Strategies.Example;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class ExampleStrategyViewModelTests
{
    [Fact]
    public void Appends_bar_when_subscription_emits()
    {
        var repo = Substitute.For<IMarketDataRepository>();
        var state = new BehaviorSubject<ConnectionState>(ConnectionState.Connected);
        repo.ConnectionState.Returns(state);

        var vm = new ExampleStrategyViewModel(repo, NullLogger<ExampleStrategyViewModel>.Instance);
        vm.Bars.Should().BeEmpty();

        // Drive the public AppendBar seam — this is the same path the streaming pipeline uses.
        var bar1 = new Bar(DateTime.UtcNow, 100, 101, 99.5, 100.5, 1000);
        var bar2 = new Bar(DateTime.UtcNow.AddMinutes(3), 100.5, 102, 100.4, 101.7, 1500);

        vm.AppendBar(bar1);
        vm.AppendBar(bar2);

        vm.Bars.Should().HaveCount(2);
        vm.Bars[0].Should().Be(bar1);
        vm.Bars[1].Should().Be(bar2);
        vm.LastPrice.Should().Be(101.7);
    }

    [Fact]
    public void Drops_oldest_bar_when_capacity_exceeded()
    {
        var repo = Substitute.For<IMarketDataRepository>();
        repo.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));

        var vm = new ExampleStrategyViewModel(repo, NullLogger<ExampleStrategyViewModel>.Instance);

        for (int i = 0; i < ExampleStrategyViewModel.MaxBarsRetained + 5; i++)
            vm.AppendBar(new Bar(DateTime.UtcNow.AddMinutes(i), i, i + 1, i - 1, i + 0.5, 100));

        vm.Bars.Should().HaveCount(ExampleStrategyViewModel.MaxBarsRetained);
        vm.Bars[0].Open.Should().Be(5);
    }
}
