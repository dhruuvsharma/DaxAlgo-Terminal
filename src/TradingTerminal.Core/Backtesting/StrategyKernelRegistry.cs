namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// A catalog entry for one strategy kernel: a stable <see cref="Id"/> (what <see cref="RunSpec"/>,
/// the CLI, and the Python seam reference), display metadata, the tunable <see cref="Schema"/>, and a
/// factory that builds a fresh instance (kernels are stateful, so every run gets its own). Native C#
/// kernels and adapter-wrapped legacy strategies are both expressed as descriptors, so the rest of
/// the platform treats them uniformly.
/// </summary>
public sealed record StrategyKernelDescriptor(
    string Id,
    string Name,
    string Description,
    StrategyParameterSchema Schema,
    Func<IStrategyKernel> Create);

/// <summary>Discovers strategy kernels by id and builds instances. The Studio catalog, the optimizer,
/// and the headless CLI all resolve kernels through this rather than referencing concretes.</summary>
public interface IStrategyKernelRegistry
{
    IReadOnlyList<StrategyKernelDescriptor> All { get; }
    StrategyKernelDescriptor? Find(string id);
    IStrategyKernel Create(string id);
    bool TryCreate(string id, out IStrategyKernel kernel);
}

/// <summary>In-memory registry seeded from a set of descriptors at composition time. Ids are matched
/// case-insensitively.</summary>
public sealed class StrategyKernelRegistry : IStrategyKernelRegistry
{
    private readonly Dictionary<string, StrategyKernelDescriptor> _byId;

    public StrategyKernelRegistry(IEnumerable<StrategyKernelDescriptor> descriptors) =>
        _byId = descriptors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<StrategyKernelDescriptor> All => _byId.Values.ToList();

    public StrategyKernelDescriptor? Find(string id) =>
        _byId.TryGetValue(id, out var d) ? d : null;

    public IStrategyKernel Create(string id) =>
        Find(id)?.Create() ?? throw new KeyNotFoundException($"No strategy kernel registered with id '{id}'.");

    public bool TryCreate(string id, out IStrategyKernel kernel)
    {
        if (_byId.TryGetValue(id, out var d)) { kernel = d.Create(); return true; }
        kernel = null!;
        return false;
    }
}
