using System.Reflection;
using System.Runtime.Loader;
using DaxAlgo.Sdk;
using DaxAlgo.Strategy.Bundle;
using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Backtest.Worker;

internal sealed class BundleStrategyLoadContext(
    IReadOnlyDictionary<string, byte[]> privateAssemblies) : AssemblyLoadContext(
        $"daxstrategy-{Guid.NewGuid():N}",
        isCollectible: true)
{
    private readonly Dictionary<string, byte[]> _privateAssemblies =
        new(privateAssemblies, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, Assembly> SharedAssemblies =
        new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
        {
            [typeof(IStrategyEngineFactory).Assembly.GetName().Name!] = typeof(IStrategyEngineFactory).Assembly,
            [typeof(IBacktestStrategy).Assembly.GetName().Name!] = typeof(IBacktestStrategy).Assembly,
        };

    private static readonly IReadOnlySet<string> PlatformAssemblies = LoadPlatformAssemblyNames();

    public Assembly LoadEngine(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return LoadFromStream(stream);
    }

    public void ClearAndUnload()
    {
        _privateAssemblies.Clear();
        Unload();
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name
                         ?? throw new FileLoadException("A strategy dependency has no simple assembly name.");
        if (SharedAssemblies.TryGetValue(simpleName, out var shared))
        {
            EnsureIdentity(assemblyName, shared.GetName());
            return shared;
        }

        if (_privateAssemblies.TryGetValue(simpleName, out var bytes))
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var loaded = LoadFromStream(stream);
            EnsureIdentity(assemblyName, loaded.GetName());
            return loaded;
        }

        if (!PlatformAssemblies.Contains(simpleName))
            throw new FileNotFoundException(
                $"Strategy dependency '{simpleName}' is neither in the verified engine closure, a shared contract, nor the worker runtime framework.");

        var external = Default.LoadFromAssemblyName(assemblyName);
        EnsureIdentity(assemblyName, external.GetName());
        return external;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        throw new DllNotFoundException(
            $"Native dependency '{unmanagedDllName}' is not permitted in a .daxstrategy engine.");

    public static bool IsWorkerExternalAssemblyAvailable(string simpleName) =>
        SharedAssemblies.ContainsKey(simpleName) || PlatformAssemblies.Contains(simpleName);

    private static void EnsureIdentity(AssemblyName requested, AssemblyName resolved)
    {
        var requestedToken = requested.GetPublicKeyToken() ?? [];
        var resolvedToken = resolved.GetPublicKeyToken() ?? [];
        if (!string.Equals(requested.Name, resolved.Name, StringComparison.OrdinalIgnoreCase) ||
            requested.Version != resolved.Version ||
            !string.Equals(
                NormalizeCulture(requested.CultureName),
                NormalizeCulture(resolved.CultureName),
                StringComparison.OrdinalIgnoreCase) ||
            requested.ContentType != resolved.ContentType ||
            !requestedToken.AsSpan().SequenceEqual(resolvedToken))
        {
            throw new FileLoadException(
                $"Resolved assembly '{resolved.FullName}' does not match requested identity '{requested.FullName}'.");
        }
    }

    private static string NormalizeCulture(string? culture) =>
        string.IsNullOrEmpty(culture) ? "neutral" : culture;

    private static IReadOnlySet<string> LoadPlatformAssemblyNames()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            ?? [];
        return paths
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }
}
