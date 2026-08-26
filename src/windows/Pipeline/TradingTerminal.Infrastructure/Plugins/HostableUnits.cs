using System.Reflection;
using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Finds the units in a plugin assembly that this host could actually run: concrete
/// <see cref="IStrategyKernel"/> and <see cref="IVisualizer"/> implementations with a public
/// parameterless constructor.
///
/// <para>This exists because of a gap that made every authored artifact a dead file. The loader
/// recognised exactly one thing — a public parameterless <c>IStrategyPlugin</c> contributing
/// <c>ITradingStrategy</c> registrations — and an assembly containing none was not merely left
/// unregistered but never recorded as loaded at all. Since Hyperion emits kernels and visualizers and
/// no plugin entry point, everything it produced installed to a folder that the next start walked past
/// in silence.</para>
///
/// <para><b>It tests types and instantiates nothing.</b> The question here is only whether the host
/// should count this assembly as a plugin; building a registration means constructing the unit to read
/// its schema, and that belongs above, after the trust and scan gates have had their say — in the layer
/// that owns the catalog and can decide what a failure to construct should look like.</para>
///
/// <para>Kept in this layer rather than beside the registries because the loader must answer the
/// "did anything load?" question itself, and the registries live in the UI assembly the loader has no
/// reference to — deliberately, since a plugin loader that could reach into the catalog would be a
/// plugin loader with an opinion about presentation.</para>
/// </summary>
public static class HostableUnits
{
    /// <summary>Every kernel and visualizer type the host could construct.</summary>
    public static IReadOnlyList<Type> In(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var found = new List<Type>();
        foreach (var type in Types(assembly))
        {
            if (CanHost(type)) found.Add(type);
        }

        return found;
    }

    /// <summary>True when the assembly carries at least one — the loader's "this is a plugin after
    /// all" test, answered without constructing anything.</summary>
    public static bool Any(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in Types(assembly))
        {
            if (CanHost(type)) return true;
        }

        return false;
    }

    /// <summary>A concrete kernel or visualizer the host can build itself. The parameterless
    /// constructor is not a style preference: the host constructs one per opened window and has no
    /// container the unit could ask for dependencies from.</summary>
    public static bool CanHost(Type? type) =>
        type is { IsClass: true, IsAbstract: false }
        && (typeof(IStrategyKernel).IsAssignableFrom(type) || typeof(IVisualizer).IsAssignableFrom(type))
        && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>A partially-loadable assembly still has usable types; the exception carries them. One
    /// unresolvable type must not hide the rest, the same way one bad plugin never blocks the host.</summary>
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
