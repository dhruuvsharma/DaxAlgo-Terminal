namespace TradingTerminal.Core.Strategies;

/// <summary>
/// The strategy catalog: what the Strategies pane lists, and how a card becomes a live window. The shell
/// never constructs strategy types directly — adding a strategy is one DI registration.
/// <para>
/// It is also mutable at runtime. A strategy authored in the AI builder is compiled, IL-scanned and
/// registered through <see cref="Register"/> while the app runs — that is what lets a user add a strategy
/// without recompiling, or even restarting, the host. Shells bind <see cref="All"/> and refresh on
/// <see cref="Changed"/>.
/// </para>
/// </summary>
public interface IStrategyFactory
{
    IReadOnlyList<ITradingStrategy> All { get; }

    /// <summary>
    /// Builds a fresh host for the given strategy id.
    /// Throws <see cref="KeyNotFoundException"/> if the id is not registered.
    /// </summary>
    StrategyHost Create(string strategyId);

    /// <summary>
    /// Adds (or replaces, by id) a strategy at runtime. The caller owns the trust gate: the only in-tree
    /// caller is the authoring installer, which compiles through the same IL policy scan the plugin
    /// loader applies, and only once the user has pressed Compile &amp; Register.
    /// </summary>
    void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration);

    /// <summary>Fires when a strategy is added after startup, so a bound catalog can show it at once.</summary>
    event EventHandler<StrategyCatalogChange>? Changed;
}

/// <summary>A strategy appeared in the catalog after startup (<paramref name="Replaced"/> when it took
/// over an existing id — the user regenerated it).</summary>
public sealed record StrategyCatalogChange(ITradingStrategy Strategy, bool Replaced);
