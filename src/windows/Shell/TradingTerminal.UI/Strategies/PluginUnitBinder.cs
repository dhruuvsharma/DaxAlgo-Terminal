using System.Reflection;
using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Strategies;

/// <summary>What binding a set of loaded plugins produced.</summary>
/// <param name="Strategies">How many kernels reached the strategy registry.</param>
/// <param name="Visualizers">How many visualizers reached the visualizer registry.</param>
/// <param name="Skipped">Units that could not be registered, and why — one line each.</param>
public sealed record PluginUnitBindResult(
    int Strategies,
    int Visualizers,
    IReadOnlyList<string> Skipped)
{
    public int Total => Strategies + Visualizers;

    public override string ToString() =>
        Total == 0 && Skipped.Count == 0
            ? "No authored units in the installed plugins."
            : $"{Strategies} strategy(s) and {Visualizers} visualizer(s) from installed plugins"
              + (Skipped.Count > 0 ? $", {Skipped.Count} skipped." : ".");
}

/// <summary>
/// Puts the kernels and visualizers found in installed plugins into the registries that can open them.
///
/// <para>This is the step that makes an installed strategy survive a restart. The loader gates an
/// assembly and reports it; the catalog needs a registration built from its types — and those are two
/// different layers on purpose, because a plugin loader that could reach into the catalog would be a
/// plugin loader with an opinion about presentation, and the catalog lives above it.</para>
///
/// <para><b>Identity comes from the plugin manifest, not from the type.</b> The obvious default —
/// <c>type.FullName</c> — collides the moment two authored strategies are both called
/// <c>MomentumKernel</c> in no namespace, which is exactly what a model writing single-file units
/// produces; registering by id replaces, so the second install would silently take the first one's
/// place in the catalog. The manifest id is the thing that is actually unique per package.</para>
///
/// <para><b>The display name comes from there too</b>, and until this took one it did not: only the id
/// was passed on, so <c>StrategyKernelDescriptors.FromType</c> fell back to humanising the type name
/// and a strategy the user had called "Shelf momentum" appeared in their catalog as "Restart". The id
/// was right, which is why it went unnoticed — the existing test asserted the id and was named as
/// though it asserted the name.</para>
/// </summary>
public static class PluginUnitBinder
{
    /// <summary>
    /// Binds every hostable unit in <paramref name="assemblies"/>, keyed by the plugin id each came
    /// from. Never throws: a unit whose constructor fails is reported and skipped, because one bad
    /// plugin must not cost the user the rest of their catalog.
    /// </summary>
    /// <param name="assemblies">Plugin id, the name from its manifest, and the loaded assembly — all
    /// three from the load report. A null or blank name falls back to the humanised type name, which
    /// is what a package with no manifest gets.</param>
    /// <param name="strategies">Where kernels go.</param>
    /// <param name="visualizers">Where visualizers go.</param>
    public static PluginUnitBindResult Bind(
        IEnumerable<(string PluginId, string? DisplayName, Assembly Image)> assemblies,
        IStrategyKernelRegistry? strategies,
        IVisualizerRegistry? visualizers)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var skipped = new List<string>();
        var kernelCount = 0;
        var visualizerCount = 0;

        foreach (var (pluginId, displayName, image) in assemblies)
        {
            if (image is null) continue;

            // Counted per plugin, so the first unit keeps the clean id and only a genuine second one in
            // the same package needs qualifying. A package almost always carries exactly one.
            var seen = 0;

            foreach (var type in Types(image))
            {
                var isVisualizer = typeof(IVisualizer).IsAssignableFrom(type);
                var isKernel = typeof(IStrategyKernel).IsAssignableFrom(type);
                if (!isKernel && !isVisualizer) continue;
                if (type is not { IsClass: true, IsAbstract: false }) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;

                var id = seen == 0 ? pluginId : $"{pluginId}.{type.Name}";

                // The manifest names the PACKAGE, so it belongs to the first unit in it. A second unit
                // in the same package is a different thing and keeps its own humanised type name
                // rather than borrowing a title that is now wrong for it.
                var name = seen == 0 && !string.IsNullOrWhiteSpace(displayName) ? displayName : null;
                seen++;

                try
                {
                    if (isVisualizer && visualizers is not null)
                    {
                        visualizers.Register(VisualizerDescriptors.FromType(type, id, name));
                        visualizerCount++;
                    }
                    else if (isKernel && strategies is not null)
                    {
                        strategies.Register(StrategyKernelDescriptors.FromType(type, id, name));
                        kernelCount++;
                    }
                }
                catch (Exception ex)
                {
                    // Building a registration constructs the unit to read its schema. A constructor that
                    // throws is a broken unit, not a broken host — and the user needs the other cards.
                    skipped.Add($"{id}: {ex.Message}");
                }
            }
        }

        return new PluginUnitBindResult(kernelCount, visualizerCount, skipped);
    }

    /// <summary>A partially-loadable assembly still has usable types; the exception carries them.</summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
