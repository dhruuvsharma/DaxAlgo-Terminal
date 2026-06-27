using System.IO;
using System.Reflection;
using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>Metadata about a plugin that was successfully discovered and registered.</summary>
public sealed record LoadedPlugin(string Name, string TargetSdkVersion, string AssemblyPath);

/// <summary>
/// Discovers and loads strategy plugins. A plugin is a folder under the plugins root containing a
/// <c>&lt;foldername&gt;.dll</c> that exposes one public parameterless <see cref="IStrategyPlugin"/>.
/// Each plugin loads in its own collectible <see cref="PluginLoadContext"/> (host contract assemblies
/// shared with the default context). A plugin's declared <see cref="IStrategyPlugin.TargetSdkVersion"/>
/// must be compatible with the host SDK (<see cref="SdkInfo.Version"/>) or it is rejected. One bad
/// plugin never blocks the host — per-plugin failures are reported and skipped.
/// <para>
/// The <see cref="RegisterFromAssembly"/> path (discovery + version check + Register) is separated
/// from file/ALC loading so it is unit-testable directly against an already-loaded assembly.
/// </para>
/// </summary>
public static class PluginLoader
{
    /// <summary>Scans <paramref name="pluginsRoot"/> and registers each plugin into
    /// <paramref name="services"/> using the <see cref="PluginTrustPolicy.Permissive"/> policy (the
    /// open-core dev flow — unsigned local plugins load, signatures aren't inspected). A missing
    /// directory or no plugins is a no-op.</summary>
    public static IReadOnlyList<LoadedPlugin> LoadInto(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        Action<string, Exception>? onError = null) =>
        LoadInto(services, pluginsRoot, hostSdkVersion, PluginTrustPolicy.Permissive, DefaultInspector, onError);

    /// <summary>Scans <paramref name="pluginsRoot"/> and registers each plugin that satisfies
    /// <paramref name="policy"/> into <paramref name="services"/>. Trust is checked BEFORE the
    /// assembly is loaded — an untrusted plugin's code never executes. A missing directory or no
    /// plugins is a no-op (empty list); a rejected or faulted plugin is reported via
    /// <paramref name="onError"/> and skipped, never blocking the host.</summary>
    public static IReadOnlyList<LoadedPlugin> LoadInto(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        Action<string, Exception>? onError = null)
    {
        var loaded = new List<LoadedPlugin>();
        if (!Directory.Exists(pluginsRoot)) return loaded;

        foreach (var dll in EnumeratePluginAssemblies(pluginsRoot))
        {
            try
            {
                // ── Trust gate (before loading any code) ──────────────────────────────────────────
                var manifest = PluginManifest.TryRead(Path.GetDirectoryName(dll)!);
                // Inspect the signature only when the policy actually needs it (the permissive dev
                // flow skips Authenticode entirely).
                var signature = policy.RequireSignature ? inspector.Inspect(dll) : PluginSignature.Unsigned;
                if (!policy.Allows(signature, manifest is not null, out var reason))
                    throw new PluginRejectedException(dll, reason!);

                // ── Load + register ──────────────────────────────────────────────────────────────
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                if (RegisterFromAssembly(asm, services, hostSdkVersion) is { } meta)
                    loaded.Add(meta);
            }
            catch (Exception ex)
            {
                onError?.Invoke(dll, ex);
            }
        }
        return loaded;
    }

    /// <summary>The default signature inspector: real Authenticode on Windows, null (always unsigned)
    /// elsewhere. Only consulted when a policy requires signatures.</summary>
    private static IPluginSignatureInspector DefaultInspector =>
        OperatingSystem.IsWindows() ? new AuthenticodeSignatureInspector() : new NullSignatureInspector();

    /// <summary>Finds the single public <see cref="IStrategyPlugin"/> in <paramref name="assembly"/>,
    /// checks its version against <paramref name="hostSdkVersion"/>, and invokes
    /// <see cref="IStrategyPlugin.Register"/>. Returns <c>null</c> when the assembly contains no plugin
    /// type. Throws <see cref="PluginIncompatibleException"/> on a version mismatch.</summary>
    public static LoadedPlugin? RegisterFromAssembly(Assembly assembly, IServiceCollection services, string hostSdkVersion)
    {
        var pluginType = assembly.GetExportedTypes().FirstOrDefault(t =>
            typeof(IStrategyPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
        if (pluginType is null) return null;

        var plugin = (IStrategyPlugin)Activator.CreateInstance(pluginType)!;
        if (!IsCompatible(plugin.TargetSdkVersion, hostSdkVersion))
            throw new PluginIncompatibleException(plugin.Name, plugin.TargetSdkVersion, hostSdkVersion);

        var path = SafeLocation(assembly);
        plugin.Register(new PluginRegistrar(services, new PluginContext(plugin.Name, path, plugin.TargetSdkVersion)));
        return new LoadedPlugin(plugin.Name, plugin.TargetSdkVersion, path);
    }

    /// <summary>Each plugin lives in its own subfolder; the main assembly is <c>&lt;foldername&gt;.dll</c>
    /// by convention. Private dependencies in the folder are resolved within the plugin's load context,
    /// not treated as separate plugins.</summary>
    internal static IEnumerable<string> EnumeratePluginAssemblies(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var candidate = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>Semver compatibility. Pre-1.0 (host major == 0) the contract is unstable, so require an
    /// exact major.minor match; from 1.0 on, a matching major is compatible.</summary>
    public static bool IsCompatible(string pluginVersion, string hostVersion)
    {
        var (pMaj, pMin) = ParseMajorMinor(pluginVersion);
        var (hMaj, hMin) = ParseMajorMinor(hostVersion);
        return hMaj == 0 ? pMaj == 0 && pMin == hMin : pMaj == hMaj;
    }

    private static (int Major, int Minor) ParseMajorMinor(string version)
    {
        var core = (version ?? string.Empty).Split('-', '+')[0];
        var parts = core.Split('.');
        int.TryParse(parts.ElementAtOrDefault(0), out var major);
        int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        return (major, minor);
    }

    private static string SafeLocation(Assembly assembly)
    {
        try { return assembly.Location; } catch { return string.Empty; }
    }
}

/// <summary>Default <see cref="IPluginRegistrar"/> — registers straight into the host service collection.</summary>
internal sealed class PluginRegistrar(IServiceCollection services, PluginContext context) : IPluginRegistrar
{
    public IServiceCollection Services { get; } = services;
    public PluginContext Context { get; } = context;
}

/// <summary>Thrown when a plugin's target SDK version is incompatible with the host SDK.</summary>
public sealed class PluginIncompatibleException(string pluginName, string pluginVersion, string hostVersion)
    : Exception($"Plugin '{pluginName}' targets DaxAlgo.Sdk {pluginVersion}, which is incompatible with host SDK {hostVersion}.")
{
    public string PluginName { get; } = pluginName;
    public string PluginVersion { get; } = pluginVersion;
    public string HostVersion { get; } = hostVersion;
}

/// <summary>Thrown when a plugin fails the <see cref="PluginTrustPolicy"/> (unsigned, untrusted
/// signer, or missing required manifest). The plugin's code is NOT loaded.</summary>
public sealed class PluginRejectedException(string assemblyPath, string reason)
    : Exception($"Plugin '{assemblyPath}' rejected by trust policy: {reason}.")
{
    public string AssemblyPath { get; } = assemblyPath;
    public string Reason { get; } = reason;
}
