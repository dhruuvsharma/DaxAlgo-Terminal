namespace TradingTerminal.UI.Strategies;

/// <summary>What a catalog card needs to know about a unit hosted above this library.</summary>
/// <param name="Id">Stable id; the card and the Open verbs use it.</param>
/// <param name="DisplayName">What the card is called.</param>
/// <param name="Description">What the card says.</param>
/// <param name="IsStrategy">True for a unit that trades.</param>
/// <param name="Version">Changes whenever the unit is registered again, so a rebuilt unit replaces its
/// card even when its name did not change.</param>
public sealed record HostedCatalogUnit(string Id, string DisplayName, string Description, bool IsStrategy, int Version = 0);

/// <summary>
/// Units the catalog shows whose runtime lives above this library — the Blocks SDK's, which run in the
/// Blocks runtime with a WebView2 page.
///
/// <para>An interface here and an adapter up there, because this library sits below both: it hosts the
/// widget SDK's chrome and must not reach the runtimes that host other kinds of unit.</para>
/// </summary>
public interface IHostedUnitCatalog
{
    IReadOnlyList<HostedCatalogUnit> All { get; }

    /// <summary>Fires when the set changes.</summary>
    event EventHandler? Changed;
}
