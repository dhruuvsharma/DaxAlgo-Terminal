using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// DI-backed factory. Each registered strategy must also register a
/// <see cref="StrategyFactoryRegistration"/> describing how to build its (view, vm) pair.
/// <para>
/// Portable (WPF-free) so both the WPF shell and the Avalonia head open strategies through the
/// same seam — the shell never names a concrete strategy type. The view may be a UserControl
/// (rendered in a tab/host) or a Window (shown standalone); each UI head decides how to host it.
/// </para>
/// </summary>
public sealed class StrategyFactory : IStrategyFactory
{
    /// <summary>
    /// Assigns the resolved view-model to the resolved view's <c>DataContext</c>. Defaults to a
    /// reflection-based assignment that works for both WPF (<c>FrameworkElement.DataContext</c>) and
    /// Avalonia (<c>StyledElement.DataContext</c>), keeping this factory free of any UI framework
    /// reference. A UI head may replace it with a typed binder if desired.
    /// </summary>
    public static Action<object, object> BindViewModel { get; set; } = DefaultBind;

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
        var vm = reg.ViewModelFactory(_provider);
        var view = reg.ViewFactory(_provider);
        BindViewModel(view, vm);
        return new StrategyHost(strategyId, meta.DisplayName, view, vm);
    }

    private static void DefaultBind(object view, object viewModel) =>
        view.GetType().GetProperty("DataContext")?.SetValue(view, viewModel);
}
