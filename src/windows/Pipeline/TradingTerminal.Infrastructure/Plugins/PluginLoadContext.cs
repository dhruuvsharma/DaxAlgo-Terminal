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
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginMainAssemblyPath)
        : base(name: $"Plugin:{Path.GetFileNameWithoutExtension(pluginMainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginMainAssemblyPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Share the host contract surface (return null -> default context resolves it).
        if (IsHostContract(assemblyName.Name)) return null;

        // deps.json-driven resolution first; a private dep staged FLAT next to the plugin assembly
        // (HelixToolkit.Wpf for the 3D-cube plugins) must still resolve when the deps.json is
        // missing or doesn't cover it — a staged folder is not a full publish layout.
        var path = _resolver.ResolveAssemblyToPath(assemblyName)
            ?? ProbePluginDirectory(assemblyName.Name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    private string? ProbePluginDirectory(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        var candidate = Path.Combine(_pluginDirectory, simpleName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>True for assemblies whose type identity MUST be shared between host and plugin for the
    /// registration contract to typecheck — and for a WPF plugin's window to derive from the SAME
    /// MahApps/ScottPlot/UI base types — across the load-context boundary. The host already has these
    /// loaded, so deferring to the default context gives one shared identity (and one copy of WPF's
    /// per-assembly theme/resource state).</summary>
    internal static bool IsHostContract(string? simpleName) =>
        simpleName is not null &&
        (simpleName.StartsWith("DaxAlgo.Sdk", StringComparison.Ordinal) ||
         simpleName.StartsWith("TradingTerminal.", StringComparison.Ordinal) ||
         simpleName.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
         simpleName.StartsWith("CommunityToolkit.", StringComparison.Ordinal) ||
         // Shared WPF UI toolkits a strategy plugin's window/charts build on (host provides them).
         simpleName.StartsWith("MahApps.", StringComparison.Ordinal) ||
         simpleName.StartsWith("ScottPlot", StringComparison.Ordinal) ||
         simpleName.StartsWith("ControlzEx", StringComparison.Ordinal)); // MahApps dependency
}
