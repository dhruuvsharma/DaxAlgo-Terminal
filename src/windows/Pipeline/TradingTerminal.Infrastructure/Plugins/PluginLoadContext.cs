using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> for one plugin assembly. Host-owned CONTRACT
/// assemblies (DaxAlgo.Sdk*, TradingTerminal.*, Microsoft.Extensions.*, CommunityToolkit.*) are
/// deliberately NOT loaded privately — <see cref="Load"/> returns <c>null</c> for them so the
/// DEFAULT context resolves them. That gives the plugin's types the SAME identity as the host's:
/// without it, the host's <c>StrategyFactory</c> would see the plugin's <c>ITradingStrategy</c> as a
/// different type and never match it. Only the plugin's own private dependencies load from the
/// plugin folder (resolved via the deps.json next to the plugin).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginMainAssemblyPath)
        : base(name: $"Plugin:{Path.GetFileNameWithoutExtension(pluginMainAssemblyPath)}", isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the host contract surface (return null -> default context resolves it).
        if (IsHostContract(assemblyName.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>True for assemblies whose type identity MUST be shared between host and plugin for the
    /// registration contract to typecheck across the load-context boundary.</summary>
    internal static bool IsHostContract(string? simpleName) =>
        simpleName is not null &&
        (simpleName.StartsWith("DaxAlgo.Sdk", StringComparison.Ordinal) ||
         simpleName.StartsWith("TradingTerminal.", StringComparison.Ordinal) ||
         simpleName.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
         simpleName.StartsWith("CommunityToolkit.", StringComparison.Ordinal));
}
