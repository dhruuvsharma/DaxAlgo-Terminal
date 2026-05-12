namespace TradingTerminal.App.Backtest;

/// <summary>
/// The runtime source of available backtest strategies. Consumers (BacktestViewModel,
/// signal-host registration) inject this rather than reaching into the static
/// <c>BacktestStrategyCatalog</c> directly. The default DI registration seeds the
/// registry from the catalog list; a custom registration could swap in a filtered or
/// dynamically-extended set without touching consumers.
/// </summary>
public interface IBacktestStrategyRegistry
{
    IReadOnlyList<BacktestStrategyOption> All { get; }

    /// <summary>Look up a strategy by id, or null if not registered.</summary>
    BacktestStrategyOption? Find(string id);
}

internal sealed class BacktestStrategyRegistry : IBacktestStrategyRegistry
{
    public BacktestStrategyRegistry(IEnumerable<BacktestStrategyOption> options)
    {
        All = options.ToArray();
        _byId = All.ToDictionary(o => o.Id, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, BacktestStrategyOption> _byId;
    public IReadOnlyList<BacktestStrategyOption> All { get; }
    public BacktestStrategyOption? Find(string id) =>
        _byId.TryGetValue(id, out var o) ? o : null;
}
