# TradingTerminal.Infrastructure / Plugins — public API surface

Generated 2026-07-10. Declaration lines only; multi-line signatures show their first line;
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

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginInstaller.cs
```cs
   10: public sealed record PluginHostContext(
   16: public sealed record PluginInstallResult(bool Success, string Message, string? InstalledPath = null);
   30: public static class PluginInstaller
   35: public static PluginInstallResult InstallFromDll(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginLoadContext.cs
```cs
   20: public PluginLoadContext(string pluginMainAssemblyPath)
   24: protected override Assembly? Load(AssemblyName assemblyName)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginLoader.cs
```cs
    9: public sealed record LoadedPlugin(string Name, string TargetSdkVersion, string AssemblyPath);
   23: public static class PluginLoader
   38: public static IReadOnlyList<LoadedPlugin> LoadInto(
   50: public static IReadOnlyList<LoadedPlugin> LoadInto(
   97: public static LoadedPlugin? RegisterFromAssembly(Assembly assembly, IServiceCollection services, string hostSdkVersion)
  126: public static bool IsCompatible(string pluginVersion, string hostVersion)
  151: public IServiceCollection Services { get; } = services;
  152: public PluginContext Context { get; } = context;
  156: public sealed class PluginIncompatibleException(string pluginName, string pluginVersion, string hostVersion)
  159: public string PluginName { get; } = pluginName;
  160: public string PluginVersion { get; } = pluginVersion;
  161: public string HostVersion { get; } = hostVersion;
  166: public sealed class PluginRejectedException(string assemblyPath, string reason)
  169: public string AssemblyPath { get; } = assemblyPath;
  170: public string Reason { get; } = reason;
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

## src/windows/Pipeline/TradingTerminal.Infrastructure/Plugins/PluginTrustPolicy.cs
```cs
   14: public sealed record PluginTrustPolicy(
   21: public static PluginTrustPolicy Permissive { get; } =
   26: public static PluginTrustPolicy Curated(IEnumerable<string> trustedThumbprints) =>
   33: public bool Allows(PluginSignature signature, bool hasManifest, out string? reason)
```
