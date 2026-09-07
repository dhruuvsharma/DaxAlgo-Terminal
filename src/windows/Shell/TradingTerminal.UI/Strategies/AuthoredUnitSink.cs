using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Puts a compiled unit in the registry that can actually run it.
///
/// <para>The two kinds go to separate registries rather than one, because the host runs them
/// differently: a strategy is given a virtual book and a visualizer is not. Collapsing that distinction
/// to save a type is how a visualizer ends up with a route to trading.</para>
/// </summary>
public sealed class AuthoredUnitSink(
    IStrategyKernelRegistry kernels,
    IVisualizerRegistry visualizers) : IAuthoredUnitSink
{
    private readonly IStrategyKernelRegistry _kernels =
        kernels ?? throw new ArgumentNullException(nameof(kernels));

    private readonly IVisualizerRegistry _visualizers =
        visualizers ?? throw new ArgumentNullException(nameof(visualizers));

    public string Register(AuthoredUnit unit, string id, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (string.IsNullOrWhiteSpace(id)) return "Give the unit an id before registering it.";

        if (unit.UsesRetiredContract)
        {
            return $"'{unit.Type.Name}' implements {unit.ContractName}, which the catalog cannot host. "
                 + "Implement IStrategyKernel.";
        }

        try
        {
            if (unit.Kind == AuthoringKind.Visualizer)
            {
                // The name goes through, as it does for a strategy one line below. It did not, and the
                // consequence is the one PluginUnitBinder already records from the strategy side: the
                // descriptor fell back to humanising the type, so the message said "Registered
                // visualizer 'Order flow pressure'" and the card underneath it read "Pressure Kernel".
                _visualizers.Register(VisualizerDescriptors.FromType(unit.Type, id, displayName));
                return $"Registered visualizer '{displayName ?? id}'. Open it from the catalog.";
            }

            _kernels.Register(StrategyKernelDescriptors.FromType(unit.Type, id, displayName));
            return $"Registered strategy '{displayName ?? id}'. Open it from the catalog.";
        }
        catch (Exception ex)
        {
            // The contract says never throw. The code compiled and was verified; a registration fault is
            // worth reporting but must not cost the author their session.
            return $"Compiled and verified, but registration failed: {ex.Message}";
        }
    }
}
