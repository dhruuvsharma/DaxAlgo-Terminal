# DaxAlgo.Sdk — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Sdk/DaxAlgo.Sdk/AuthoredPlugin.cs
```cs
   19: public sealed record AuthoredStrategyTypes(
   26: public bool HasLiveWindow => Descriptor is not null && ViewModel is not null && View is not null;
   31: public bool CanComposeLiveWindow => Descriptor is not null && ViewModel is not null;
   38: public static AuthoredStrategyTypes DiscoverIn(Assembly assembly)
   83: public static class AuthoredPluginBootstrap
   85: public static void Register(IPluginRegistrar registrar, Assembly assembly, string strategyId, string displayName)
```

## src/windows/Sdk/DaxAlgo.Sdk/IPluginRegistrar.cs
```cs
   19: public interface IPluginRegistrar
   23:     IServiceCollection Services { get; }
   26:     PluginContext Context { get; }
   33: public sealed record PluginContext(string Name, string AssemblyPath, string TargetSdkVersion);
```

## src/windows/Sdk/DaxAlgo.Sdk/IStrategyPlugin.cs
```cs
   16: public interface IStrategyPlugin
   19:     string Name { get; }
   24:     string TargetSdkVersion { get; }
   27:     void Register(IPluginRegistrar registrar);
```

## src/windows/Sdk/DaxAlgo.Sdk/SdkInfo.cs
```cs
   15: public static class SdkInfo
   18: public const string Version = "0.1.1-alpha";
```
