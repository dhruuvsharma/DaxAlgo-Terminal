using System.Reflection;
using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// One registered sandbox strategy: what its catalog card says, and how to build one.
///
/// <para>The factory is what separates this from a descriptor. A card with no factory behind it opens
/// to nothing — which is precisely the failure the visualizer registry was created to end, and there is
/// no reason to reintroduce it on the strategy side.</para>
/// </summary>
/// <param name="Descriptor">The catalog card's metadata.</param>
/// <param name="Create">Builds a fresh instance. Called once per opened window, never shared.</param>
public sealed record StrategyKernelRegistration(VisualizerDescriptor Descriptor, Func<IStrategyKernel> Create)
{
    public string Id => Descriptor.Id;
}

/// <summary>
/// The runtime source of sandbox strategies — <c>IStrategyKernel</c> implementations, as opposed to the
/// retired <c>IOrderRoutedStrategy</c> entries that <c>IStrategyRegistry</c> still holds for installed
/// plugins.
///
/// <para>Deliberately the same shape as <see cref="IVisualizerRegistry"/>, for the same reason that one
/// gives: a user installing a strategy pack and a user installing a visualizer pack should not meet two
/// different mechanisms. The two contracts are near-identical, so their registration is too.</para>
///
/// <para><see cref="Changed"/> is what lets a strategy authored in Hyperion appear in the catalog
/// without a restart.</para>
/// </summary>
public interface IStrategyKernelRegistry
{
    IReadOnlyList<StrategyKernelRegistration> All { get; }

    /// <summary>Looks one up by id, or null when nothing is registered under it.</summary>
    StrategyKernelRegistration? Find(string id);

    /// <summary>Adds one, replacing any existing entry with the same id. Raises <see cref="Changed"/>.</summary>
    void Register(StrategyKernelRegistration registration);

    /// <summary>Removes one by id. Returns true if it was there. Raises <see cref="Changed"/>.</summary>
    bool Remove(string id);

    /// <summary>Fires when the set changes — a runtime author, install, or removal.</summary>
    event EventHandler? Changed;
}

/// <inheritdoc />
public sealed class StrategyKernelRegistry : IStrategyKernelRegistry
{
    private readonly List<StrategyKernelRegistration> _registrations = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<StrategyKernelRegistration> All
    {
        get { lock (_gate) return [.. _registrations]; }
    }

    public StrategyKernelRegistration? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
            return _registrations.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Register(StrategyKernelRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            // Replace rather than stack: regenerating a strategy in Hyperion should update its card, not
            // add a second one that shadows the first depending on lookup order.
            _registrations.RemoveAll(r => string.Equals(r.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
            _registrations.Add(registration);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        int removed;
        lock (_gate)
            removed = _registrations.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed > 0;
    }

    public event EventHandler? Changed;
}

/// <summary>
/// Turns a compiled type into a registration, the way <c>VisualizerDescriptors</c> does for the other
/// contract.
/// </summary>
public static class StrategyKernelDescriptors
{
    /// <summary>True when the host can run this type: a concrete <c>IStrategyKernel</c> the host can
    /// construct itself.</summary>
    public static bool CanHost(Type? type) =>
        type is { IsClass: true, IsAbstract: false }
        && typeof(IStrategyKernel).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;

    /// <summary>Builds a registration for <paramref name="type"/>, reading the schema and data
    /// requirement off a throwaway instance so the card can say what the strategy needs.</summary>
    public static StrategyKernelRegistration FromType(Type type, string? id = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!CanHost(type))
        {
            throw new ArgumentException(
                $"'{type.Name}' is not a hostable strategy: it must be a concrete IStrategyKernel with a "
                + "public parameterless constructor.",
                nameof(type));
        }

        // One instance, discarded. Reading the schema needs an object, and the host builds a fresh one
        // per window anyway — sharing this one would give every window the same mutable state.
        var probe = (IStrategyKernel)Activator.CreateInstance(type)!;

        return new StrategyKernelRegistration(
            new VisualizerDescriptor(
                id ?? type.FullName ?? type.Name,
                displayName ?? Humanise(type.Name),
                Description: $"Authored strategy · {probe.Schema.Parameters.Count} parameter(s)",
                ImagePath: null,
                DataRequirementTags: Tags(probe.DataRequirement)),
            () => (IStrategyKernel)Activator.CreateInstance(type)!);
    }

    /// <summary>Every hostable strategy in an assembly.</summary>
    public static IReadOnlyList<StrategyKernelRegistration> DiscoverIn(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var found = new List<StrategyKernelRegistration>();
        foreach (var type in Types(assembly))
        {
            if (!CanHost(type)) continue;
            try { found.Add(FromType(type)); }
            catch (Exception) { /* a type that throws in its constructor is not hostable */ }
        }

        return found;
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        // A partially-loadable assembly still has usable types; ReflectionTypeLoadException carries them.
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static IReadOnlyList<string> Tags(Core.Strategies.StrategyDataRequirement requirement)
    {
        var tags = new List<string>();
        if (requirement.HasFlag(Core.Strategies.StrategyDataRequirement.L1)) tags.Add("L1");
        if (requirement.HasFlag(Core.Strategies.StrategyDataRequirement.Bars)) tags.Add("Bars");
        if (requirement.HasFlag(Core.Strategies.StrategyDataRequirement.Depth)) tags.Add("Depth");
        if (requirement.HasFlag(Core.Strategies.StrategyDataRequirement.TradeTape)) tags.Add("Tape");
        return tags;
    }

    /// <summary>"MovingAverageCrossKernel" becomes "Moving Average Cross" — a card title, not a type name.</summary>
    private static string Humanise(string typeName)
    {
        var trimmed = typeName;
        foreach (var suffix in new[] { "Kernel", "Strategy" })
        {
            if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.Ordinal))
                trimmed = trimmed[..^suffix.Length];
        }

        var spaced = new System.Text.StringBuilder(trimmed.Length + 8);
        for (var index = 0; index < trimmed.Length; index++)
        {
            if (index > 0 && char.IsUpper(trimmed[index]) && !char.IsUpper(trimmed[index - 1]))
                spaced.Append(' ');
            spaced.Append(trimmed[index]);
        }

        return spaced.Length == 0 ? typeName : spaced.ToString();
    }
}
