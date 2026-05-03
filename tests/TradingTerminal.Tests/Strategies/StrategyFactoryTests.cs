using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TradingTerminal.App.Strategies;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategyFactoryTests
{
    [WpfFact]
    public void Resolves_registered_strategy_returns_view_and_vm()
    {
        var services = new ServiceCollection();
        var strategy = new FakeStrategy();
        services.AddSingleton<ITradingStrategy>(strategy);

        services.AddSingleton<FakeViewModel>();
        services.AddSingleton<FakeView>();

        services.AddSingleton(new StrategyFactoryRegistration(
            strategy.Id,
            sp => sp.GetRequiredService<FakeView>(),
            sp => sp.GetRequiredService<FakeViewModel>()));

        var sp = services.BuildServiceProvider();
        var factory = ActivatorUtilities.CreateInstance<StrategyFactory>(sp);

        var host = factory.Create(strategy.Id);

        host.StrategyId.Should().Be(strategy.Id);
        host.DisplayName.Should().Be(strategy.DisplayName);
        host.View.Should().BeOfType<FakeView>();
        host.ViewModel.Should().BeOfType<FakeViewModel>();
        ((FakeView)host.View).DataContext.Should().BeSameAs(host.ViewModel);
    }

    [WpfFact]
    public void Throws_when_unknown_strategy_id_requested()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var factory = new StrategyFactory(sp,
            Array.Empty<ITradingStrategy>(),
            Array.Empty<StrategyFactoryRegistration>());

        var act = () => factory.Create("does.not.exist");
        act.Should().Throw<KeyNotFoundException>();
    }

    private sealed class FakeStrategy : ITradingStrategy
    {
        public string Id => "test.fake";
        public string DisplayName => "Fake";
        public string Description => "A fake strategy for tests.";
    }

    private sealed class FakeViewModel : ViewModelBase { }
    private sealed class FakeView : UserControl { }
}
