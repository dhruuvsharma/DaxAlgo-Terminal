using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// One registered visualizer: what the catalog card says about it, and how to build one.
///
/// <para>The factory is what makes this more than a descriptor. A <see cref="VisualizerDescriptor"/>
/// alone can only be displayed — which is exactly why the catalog's "Add to chart" button did nothing
/// for so long. Registration pairs the card with something runnable.</para>
/// </summary>
/// <param name="Descriptor">The catalog card's metadata.</param>
/// <param name="Create">Builds a fresh instance. Called once per opened window, never shared.</param>
public sealed record VisualizerRegistration(VisualizerDescriptor Descriptor, Func<IVisualizer> Create)
{
    public string Id => Descriptor.Id;
}

/// <summary>
/// The runtime source of available visualizers — the counterpart to
/// <c>IStrategyRegistry</c>, and deliberately the same shape, because a user installing a
/// visualizer pack and a user installing a strategy pack should not meet two different mechanisms.
///
/// <para><see cref="Changed"/> is what lets a visualizer authored in Hyperion appear in the catalog
/// without a restart.</para>
/// </summary>
public interface IVisualizerRegistry
{
    IReadOnlyList<VisualizerRegistration> All { get; }

    /// <summary>Looks one up by id, or null when nothing is registered under it.</summary>
    VisualizerRegistration? Find(string id);

    /// <summary>Adds one, replacing any existing entry with the same id. Raises <see cref="Changed"/>.</summary>
    void Register(VisualizerRegistration registration);

    /// <summary>Removes one by id. Returns true if it was there. Raises <see cref="Changed"/>.</summary>
    bool Remove(string id);

    /// <summary>Fires when the set changes — a runtime author, install, or removal.</summary>
    event EventHandler? Changed;
}

/// <inheritdoc />
public sealed class VisualizerRegistry : IVisualizerRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VisualizerRegistration> _byId = new(StringComparer.Ordinal);

    public VisualizerRegistry()
    {
    }

    public VisualizerRegistry(IEnumerable<VisualizerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var registration in registrations)
            _byId[registration.Id] = registration;
    }

    public IReadOnlyList<VisualizerRegistration> All
    {
        get { lock (_gate) return _byId.Values.ToArray(); }
    }

    public VisualizerRegistration? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate) return _byId.TryGetValue(id, out var registration) ? registration : null;
    }

    public void Register(VisualizerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate) _byId[registration.Id] = registration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        bool removed;
        lock (_gate) removed = _byId.Remove(id);
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public event EventHandler? Changed;
}
