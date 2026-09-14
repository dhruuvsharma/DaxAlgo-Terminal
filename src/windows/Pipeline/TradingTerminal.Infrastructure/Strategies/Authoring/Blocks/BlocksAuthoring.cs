using System.Text.RegularExpressions;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// Everything the authoring pane needs to build, check and register a Blocks unit, as one dependency.
///
/// <para><b>Optional in the pane, and its presence is the switch.</b> An edition that composes one
/// authors Blocks units in Hyperion; one that does not keeps the widget SDK. A session restored from
/// before the switch is recognised by its files and stays on the SDK it was written against.</para>
/// </summary>
/// <param name="compiler">Compiles Blocks units.</param>
/// <param name="registry">Where a registered unit goes so the catalog can open it; null in an edition
/// with no catalog.</param>
/// <param name="probe">Opens the page during the gate; null on a host without WebView2.</param>
/// <param name="catalog">The cards; the embedded catalog when null.</param>
public sealed partial class BlocksAuthoring(
    BlocksUnitCompiler compiler,
    IBlocksUnitRegistry? registry = null,
    IPageProbe? probe = null,
    BlockCatalog? catalog = null)
{
    public BlocksUnitCompiler Compiler { get; } = compiler ?? throw new ArgumentNullException(nameof(compiler));

    public BlockCatalog Catalog { get; } = catalog ?? BlockCatalog.Load();

    public IBlocksUnitRegistry? Registry { get; } = registry;

    /// <summary>What the swarm says and reads for a Blocks unit.</summary>
    public BlocksSwarmDialect Dialect => new(Catalog);

    /// <summary>A fresh gate for one unit.</summary>
    public BlocksGate Gate(string unitId) => new(Compiler, unitId, probe);

    /// <summary>The critics for a Blocks unit of this kind.</summary>
    public static IReadOnlyList<CriticDefinition> Critics(AuthoringKind kind) => BlocksCritics.For(kind);

    [GeneratedRegex(@":\s*IUnit\b|\bIUnitContext\b")]
    private static partial Regex UnitMarker();

    /// <summary>True when <paramref name="files"/> are a Blocks unit: a page, or C# implementing
    /// <c>IUnit</c>.</summary>
    public static bool IsBlocksUnit(IEnumerable<StrategyFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Any(f => CodegenCodeExtractor.IsPageFile(f.Name) || UnitMarker().IsMatch(f.Content ?? string.Empty));
    }

    /// <summary>The starter the Code tab opens on. It compiles, and it shows the three things every unit
    /// does: read a setting, subscribe, send its page whole state.</summary>
    public static string Starter(AuthoringKind kind) => BlockStarters.For(kind);

    /// <summary>True when <paramref name="content"/> is one of the starters, untouched.</summary>
    public static bool IsStarter(string? content) => BlockStarters.IsStarter(content);

    /// <summary>
    /// Puts a compiled unit in the catalog, replacing any card with the same id.
    /// </summary>
    /// <returns>What to tell the user. Never throws.</returns>
    public string Register(BlocksCompileResult compiled, IReadOnlyList<StrategyFile> files, string id, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(files);

        if (!compiled.Success || compiled.Factory is null) return "The unit did not compile, so there is nothing to register.";
        if (string.IsNullOrWhiteSpace(id)) return "Give the unit an id before registering it.";
        if (Registry is null) return "Compiled and verified, but this edition has nowhere to register it.";

        try
        {
            var info = compiled.Factory().Info;
            var name = string.IsNullOrWhiteSpace(displayName) ? (string.IsNullOrWhiteSpace(info.Name) ? id : info.Name) : displayName!;
            var kind = compiled.UsesOrders ? "strategy" : "visualizer";

            Registry.Register(new BlocksUnitRegistration(
                id, name, info.Description ?? string.Empty, compiled.UsesOrders, compiled.Factory, compiled.PageFiles ?? [], files));

            return $"Registered {kind} '{name}'. Open it from the catalog.";
        }
        catch (Exception ex)
        {
            return $"Compiled and verified, but registration failed: {ex.Message}";
        }
    }
}
