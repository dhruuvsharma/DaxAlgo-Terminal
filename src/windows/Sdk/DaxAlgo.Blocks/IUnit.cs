using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Blocks;

/// <summary>
/// A strategy or visualizer: one public class with a public parameterless constructor.
///
/// <para>There is no separate strategy contract. A unit that uses the <c>orders</c> block is a
/// strategy; one that does not is a visualizer. Everything else — which instruments, which streams,
/// timers, network calls, the page it shows — is decided inside <see cref="StartAsync"/>.</para>
/// </summary>
[BlockCard(
    "unit",
    "the class you write, and the context every block hangs off",
    Does = "The host constructs the unit, calls StartAsync once with a context, and StopAsync when the window closes or settings are re-applied.",
    Needs = "One public class implementing IUnit with a public parameterless constructor. Declare user-editable values in Info.Settings.",
    Limits = "Register every handler inside StartAsync and return promptly; do not block or loop in it. All handlers run one at a time on the unit's own thread, so unit fields need no locks.",
    Types = [typeof(UnitInfo)],
    Order = 0)]
public interface IUnit
{
    /// <summary>The unit's name, description and declared settings.</summary>
    UnitInfo Info { get; }

    /// <summary>Starts the unit: read settings, subscribe to data, start timers, restore state.</summary>
    Task StartAsync(IUnitContext context, CancellationToken ct);

    /// <summary>Stops the unit; subscriptions and timers are released by the host.</summary>
    Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>What a unit is called and what the user can configure.</summary>
/// <param name="Name">Shown in the catalog and the window title.</param>
/// <param name="Description">One or two sentences for the catalog card.</param>
/// <param name="Settings">The values the user edits, built with the <c>StrategyParameter</c> factories.</param>
public sealed record UnitInfo(
    string Name,
    string Description = "",
    IReadOnlyList<StrategyParameter>? Settings = null);
