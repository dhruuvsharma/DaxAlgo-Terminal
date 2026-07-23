using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// DI-backed catalog. Each strategy registered in DI must also register a
/// <see cref="StrategyFactoryRegistration"/> describing how to build its (view, vm) pair.
/// <para>
/// Portable (WPF-free) so the shell opens strategies through one seam and never names a concrete
/// strategy type. The view may be a UserControl (rendered in a host) or a Window (shown standalone).
/// </para>
/// <para>
/// The catalog is mutable: <see cref="Register"/> adds a strategy compiled at runtime by the AI builder
/// (already IL-scanned and consented to), and <see cref="Changed"/> lets the bound Strategies pane show
/// it without a restart. Registering an existing id replaces it — regenerating a strategy updates its
/// card rather than duplicating it.
/// </para>
/// </summary>
public sealed class StrategyFactory : IStrategyFactory
{
    /// <summary>
    /// Assigns the resolved view-model to the resolved view's <c>DataContext</c>. Defaults to a
    /// reflection-based <c>DataContext</c> assignment, keeping this factory free of any UI framework
    /// reference. The WPF host may replace it with a typed binder if desired.
    /// </summary>
    public static Action<object, object> BindViewModel { get; set; } = DefaultBind;

    private readonly IServiceProvider _provider;
    private readonly object _gate = new();
    private readonly List<ITradingStrategy> _strategies;
    private readonly Dictionary<string, StrategyFactoryRegistration> _registrationsById;

    public StrategyFactory(
        IServiceProvider provider,
        IEnumerable<ITradingStrategy> strategies,
        IEnumerable<StrategyFactoryRegistration> registrations)
    {
        _provider = provider;
        _strategies = [.. strategies];
        _registrationsById = registrations.ToDictionary(r => r.StrategyId, StringComparer.Ordinal);
    }

    public IReadOnlyList<ITradingStrategy> All
    {
        get { lock (_gate) return [.. _strategies]; }
    }

    public event EventHandler<StrategyCatalogChange>? Changed;

    public void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(registration);

        if (!string.Equals(strategy.Id, registration.StrategyId, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Strategy id '{strategy.Id}' does not match its registration id '{registration.StrategyId}'.",
                nameof(registration));

        bool replaced;
        lock (_gate)
        {
            var existing = _strategies.FindIndex(s => s.Id == strategy.Id);
            replaced = existing >= 0;
            if (replaced) _strategies[existing] = strategy;
            else _strategies.Add(strategy);

            _registrationsById[strategy.Id] = registration;
        }

        Changed?.Invoke(this, new StrategyCatalogChange(strategy, replaced));
    }

    public StrategyHost Create(string strategyId)
    {
        StrategyFactoryRegistration reg;
        ITradingStrategy meta;
        lock (_gate)
        {
            if (!_registrationsById.TryGetValue(strategyId, out reg!))
                throw new KeyNotFoundException($"Strategy '{strategyId}' is not registered.");
            meta = _strategies.First(s => s.Id == strategyId);
        }

        var vm = reg.ViewModelFactory(_provider);
        var view = reg.ViewFactory(_provider);
        BindViewModel(view, vm);
        return new StrategyHost(strategyId, meta.DisplayName, view, vm);
    }

    private static void DefaultBind(object view, object viewModel) =>
        view.GetType().GetProperty("DataContext")?.SetValue(view, viewModel);
}
