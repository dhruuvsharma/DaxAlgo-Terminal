# TradingTerminal.Infrastructure / Plugins — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/AuthenticodeSignatureInspector.cs
```cs
   16: public sealed class AuthenticodeSignatureInspector : IPluginSignatureInspector
   18: public PluginSignature Inspect(string assemblyPath)
  102: public uint cbStruct;
  104: public IntPtr hFile;
  105: public IntPtr pgKnownSubject;
  111: public uint cbStruct;
  112: public IntPtr pPolicyCallbackData;
  113: public IntPtr pSIPClientData;
  114: public uint dwUIChoice;
  115: public uint fdwRevocationChecks;
  116: public uint dwUnionChoice;
  117: public IntPtr pFile;
  118: public uint dwStateAction;
  119: public IntPtr hWVTStateData;
  120: public IntPtr pwszURLReference;
  121: public uint dwProvFlags;
  122: public uint dwUIContext;
  123: public IntPtr pSignatureSettings;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/DaxPluginPackage.cs
```cs
   16: public static class DaxPluginPackage
   18: public const string Extension = ".daxplugin";
   19: public const string IndexEntryName = "package.json";
   36: public static void Write(string pluginDirectory, string mainAssemblyFileName, string outputPath)
   70: public static (string ExtractedDir, string MainAssemblyName) ExtractAndVerify(string packagePath)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/GuardedServiceCollection.cs
```cs
   36: public sealed class GuardedServiceCollection : IServiceCollection
   41: public static readonly IReadOnlyList<Type> MultiRegistrationAllowlist =
   58: public GuardedServiceCollection(IServiceCollection host, string plugin, IEnumerable<Type>? allowlist = null)
   68: public IReadOnlyList<ServiceDescriptor> Staged => _staged;
   73: public IReadOnlyList<string> Commit()
  104: public int Count => _host.Count + _staged.Count;
  106: public bool IsReadOnly => false;
  108: public ServiceDescriptor this[int index]
  118: public void Add(ServiceDescriptor item) => _staged.Add(Validate(item));
  120: public void Insert(int index, ServiceDescriptor item)
  127: public void Clear() => throw HostMutation("clear", typeof(IServiceCollection));
  129: public bool Remove(ServiceDescriptor item)
  136: public void RemoveAt(int index)
  142: public bool Contains(ServiceDescriptor item) => _host.Contains(item) || _staged.Contains(item);
  144: public int IndexOf(ServiceDescriptor item)
  152: public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
  158: public IEnumerator<ServiceDescriptor> GetEnumerator() => _host.Concat(_staged).GetEnumerator();
  166: public sealed class PluginPolicyViolationException(string pluginName, Type serviceType, string reason)
  169: public string PluginName { get; } = pluginName;
  170: public Type ServiceType { get; } = serviceType;
  171: public string Reason { get; } = reason;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginInstaller.cs
```cs
   12: public sealed record PluginHostContext(
   20: public sealed record PluginInstallResult(bool Success, string Message, string? InstalledPath = null);
   34: public static class PluginInstaller
   42: public static PluginInstallResult InstallFromDll(
   72: public static PluginInstallResult InstallFromPackage(
  111: public static PluginInstallResult Uninstall(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginLoadContext.cs
```cs
   21: public PluginLoadContext(string pluginMainAssemblyPath)
   28: protected override Assembly? Load(AssemblyName assemblyName)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginLoadReport.cs
```cs
    4: public enum PluginLoadOutcome
   37: public sealed record PluginLoadProblem(
   48: public sealed record PluginLoadReport(
   52: public static PluginLoadReport Empty { get; } = new([], []);
   56: public int AttentionCount => Problems.Count(p => p.Outcome is not PluginLoadOutcome.Disabled);
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginLoader.cs
```cs
   11: public sealed record LoadedPlugin(
   36: public static class PluginLoader
   52: public static IReadOnlyList<LoadedPlugin> LoadInto(
   65: public static IReadOnlyList<LoadedPlugin> LoadInto(
   76: public static PluginLoadReport LoadWithReport(
   86: public static PluginLoadReport LoadWithReport(
  100: public static PluginLoadReport LoadWithReport(
  240: public static LoadedPlugin? RegisterFromAssembly(Assembly assembly, IServiceCollection services, string hostSdkVersion)
  271: public static bool IsCompatible(string pluginVersion, string hostVersion)
  304: public IServiceCollection Services { get; } = services;
  305: public PluginContext Context { get; } = context;
  309: public sealed class PluginIncompatibleException(string pluginName, string pluginVersion, string hostVersion)
  312: public string PluginName { get; } = pluginName;
  313: public string PluginVersion { get; } = pluginVersion;
  314: public string HostVersion { get; } = hostVersion;
  319: public sealed class PluginRejectedException(string assemblyPath, string reason)
  322: public string AssemblyPath { get; } = assemblyPath;
  323: public string Reason { get; } = reason;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginManifest.cs
```cs
   13: public sealed record PluginManifest(
   20: public const string FileName = "plugin.json";
   31: public static PluginManifest? TryRead(string pluginDirectory)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginSignature.cs
```cs
   10: public sealed record PluginSignature(bool IsSigned, bool IsValid, string? Thumbprint, string? Subject)
   13: public static PluginSignature Unsigned { get; } = new(false, false, null, null);
   21: public interface IPluginSignatureInspector
   23:     PluginSignature Inspect(string assemblyPath);
   28: public sealed class NullSignatureInspector : IPluginSignatureInspector
   30: public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginStateStore.cs
```cs
    8: public sealed record PluginQuarantine(
   27: public sealed class PluginStateStore
   29: public const string FileName = "plugins-state.json";
   41: public PluginStateStore(string pluginsRoot)
   49: public string? LoadError { get; }
   51: public IReadOnlyList<string> Disabled { get { lock (_gate) return [.. _state.Disabled]; } }
   52: public IReadOnlyList<PluginQuarantine> Quarantined { get { lock (_gate) return [.. _state.Quarantined]; } }
   53: public IReadOnlyList<string> PendingUninstalls { get { lock (_gate) return [.. _state.PendingUninstall]; } }
   55: public bool IsDisabled(string plugin)
   60: public void SetDisabled(string plugin, bool disabled)
   70: public PluginQuarantine? QuarantineFor(string plugin)
   77: public void Quarantine(string plugin, string reason)
   87: public bool ClearQuarantine(string plugin)
   98: public void MarkPendingUninstall(string plugin)
  110: public bool ClearPendingUninstall(string plugin)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginTrustPolicy.cs
```cs
   15: public sealed record PluginTrustPolicy(
   22: public static PluginTrustPolicy Permissive { get; } =
   27: public static PluginTrustPolicy Curated(IEnumerable<string> trustedThumbprints) =>
   34: public static PluginTrustPolicy From(PluginsOptions options) => options.TrustPolicy switch
   42: public bool Allows(PluginSignature signature, bool hasManifest, out string? reason)
```
