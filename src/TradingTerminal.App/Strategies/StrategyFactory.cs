using System.Windows;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;

namespace TradingTerminal.App.Strategies;

/// <summary>
/// DI-backed factory. Each registered strategy must also register a
/// <see cref="StrategyFactoryRegistration"/> describing how to build its (view, vm) pair.
/// The view may be a <see cref="System.Windows.Controls.UserControl"/> (rendered in a tab)
/// or a <see cref="Window"/> (shown as a separate window).
/// </summary>
public sealed class StrategyFactory : IStrategyFactory
{
    private readonly IServiceProvider _provider;
    private readonly IReadOnlyDictionary<string, StrategyFactoryRegistration> _registrationsById;

    public StrategyFactory(
        IServiceProvider provider,
        IEnumerable<ITradingStrategy> strategies,
        IEnumerable<StrategyFactoryRegistration> registrations)
    {
        _provider = provider;
        All = strategies.ToArray();
        _registrationsById = registrations.ToDictionary(r => r.StrategyId, StringComparer.Ordinal);
    }

    public IReadOnlyList<ITradingStrategy> All { get; }

    public StrategyHost Create(string strategyId)
    {
        if (!_registrationsById.TryGetValue(strategyId, out var reg))
            throw new KeyNotFoundException($"Strategy '{strategyId}' is not registered.");

        var meta = All.First(s => s.Id == strategyId);
        var vm = (ViewModelBase)reg.ViewModelFactory(_provider);
        var view = (FrameworkElement)reg.ViewFactory(_provider);
        view.DataContext = vm;
        return new StrategyHost(strategyId, meta.DisplayName, view, vm);
    }
}
