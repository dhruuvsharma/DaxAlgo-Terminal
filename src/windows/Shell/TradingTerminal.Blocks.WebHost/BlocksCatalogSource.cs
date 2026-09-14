using System.Runtime.CompilerServices;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// Lists the Blocks units a registry holds in the catalog.
///
/// <para>Each registration's card carries a version drawn from the registration object itself, so a
/// unit rebuilt under the same id and name still replaces its card — the card's Open must reach the
/// new code, not the instance that was there before.</para>
/// </summary>
public sealed class BlocksCatalogSource : IHostedUnitCatalog, IDisposable
{
    private readonly IBlocksUnitRegistry _registry;

    public BlocksCatalogSource(IBlocksUnitRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _registry.Changed += OnChanged;
    }

    public IReadOnlyList<HostedCatalogUnit> All =>
        [.. _registry.All.Select(r => new HostedCatalogUnit(r.Id, r.DisplayName, r.Description, r.IsStrategy, RuntimeHelpers.GetHashCode(r)))];

    public event EventHandler? Changed;

    public void Dispose() => _registry.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
}
