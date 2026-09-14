using DaxAlgo.Blocks;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>One unit written against the Blocks SDK that the catalog can open.</summary>
/// <param name="Id">Stable id; registering the same id again replaces the card.</param>
/// <param name="DisplayName">What the card is called.</param>
/// <param name="Description">What the card says.</param>
/// <param name="IsStrategy">True when the unit uses the orders block. Decided by the compiler from the
/// source, not by what the author ticked.</param>
/// <param name="Create">Builds a fresh instance. Called once per opened window, never shared.</param>
/// <param name="PageFiles">The unit's web page (<c>ui/…</c>); empty for a unit without one.</param>
/// <param name="Sources">The unit's source files, C# and page, as they were compiled.</param>
public sealed record BlocksUnitRegistration(
    string Id,
    string DisplayName,
    string Description,
    bool IsStrategy,
    Func<IUnit> Create,
    IReadOnlyList<StrategyFile> PageFiles,
    IReadOnlyList<StrategyFile> Sources);

/// <summary>
/// The units written against the Blocks SDK that this session can open.
///
/// <para>WPF-free and below the shell on purpose: the authoring pane registers into it and the catalog
/// reads it, and neither can see the other. The same shape as the kernel and visualizer registries, so
/// the catalog binds all three the same way.</para>
/// </summary>
public interface IBlocksUnitRegistry
{
    IReadOnlyList<BlocksUnitRegistration> All { get; }

    /// <summary>Looks one up by id, or null.</summary>
    BlocksUnitRegistration? Find(string id);

    /// <summary>Adds one, replacing any existing entry with the same id. Raises <see cref="Changed"/>.</summary>
    void Register(BlocksUnitRegistration registration);

    /// <summary>Removes one by id. Returns true if it was there. Raises <see cref="Changed"/>.</summary>
    bool Remove(string id);

    /// <summary>Fires when the set changes.</summary>
    event EventHandler? Changed;
}

/// <inheritdoc />
public sealed class BlocksUnitRegistry : IBlocksUnitRegistry
{
    private readonly List<BlocksUnitRegistration> _registrations = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<BlocksUnitRegistration> All
    {
        get { lock (_gate) return [.. _registrations]; }
    }

    public BlocksUnitRegistration? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
            return _registrations.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Register(BlocksUnitRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Id);

        lock (_gate)
        {
            // Replace rather than stack: rebuilding a unit in Hyperion updates its card.
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
