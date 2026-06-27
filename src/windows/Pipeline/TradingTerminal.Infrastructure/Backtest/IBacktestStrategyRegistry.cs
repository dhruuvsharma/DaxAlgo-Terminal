using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// The runtime source of available backtest strategies. Consumers (BacktestViewModel,
/// signal-host registration, the strategy-authoring pane) inject this rather than reaching
/// into the static <c>BacktestStrategyCatalog</c> directly. The default DI registration
/// seeds the registry from the catalog list; strategies authored at runtime are added via
/// <see cref="Register"/>, and <see cref="Changed"/> lets the UI refresh its list live —
/// this is what lets a user add a strategy without recompiling the host.
/// </summary>
public interface IBacktestStrategyRegistry
{
    IReadOnlyList<BacktestStrategyOption> All { get; }

    /// <summary>Look up a strategy by id, or null if not registered.</summary>
    BacktestStrategyOption? Find(string id);

    /// <summary>Adds a strategy, replacing any existing entry with the same id. Raises <see cref="Changed"/>.</summary>
    void Register(BacktestStrategyOption option);

    /// <summary>Removes a strategy by id. Returns true if one was removed. Raises <see cref="Changed"/>.</summary>
    bool Remove(string id);

    /// <summary>Fires when the set of strategies changes (a runtime author/register/remove).</summary>
    event EventHandler? Changed;
}

internal sealed class BacktestStrategyRegistry : IBacktestStrategyRegistry
{
    public BacktestStrategyRegistry(IEnumerable<BacktestStrategyOption> options)
    {
        foreach (var option in options)
            _byId[option.Id] = option;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, BacktestStrategyOption> _byId = new(StringComparer.Ordinal);

    public IReadOnlyList<BacktestStrategyOption> All
    {
        get { lock (_gate) return _byId.Values.ToArray(); }
    }

    public BacktestStrategyOption? Find(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var option) ? option : null;
    }

    public void Register(BacktestStrategyOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        lock (_gate) _byId[option.Id] = option;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) removed = _byId.Remove(id);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public event EventHandler? Changed;
}
