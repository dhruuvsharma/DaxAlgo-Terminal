using System;
using System.Collections.Generic;
using System.IO;
using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>What the host knows about the plugin subsystem at runtime — surfaced to the Plugin
/// Manager UI. <see cref="LoadedPlugins"/> is captured at startup by the composition root.</summary>
public sealed record PluginHostContext(
    string PluginsRoot,
    PluginTrustPolicy TrustPolicy,
    IReadOnlyList<LoadedPlugin> LoadedPlugins);

/// <summary>The outcome of an install attempt.</summary>
public sealed record PluginInstallResult(bool Success, string Message, string? InstalledPath = null);

/// <summary>
/// Installs a strategy-plugin package into the host's plugins folder. A package is the plugin's main
/// assembly (<c>&lt;Name&gt;.dll</c>) plus an optional <c>plugin.json</c> and <c>.pdb</c> beside it;
/// install copies them to <c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c> (the loader's folder convention).
/// <para>
/// Validation runs WITHOUT executing the plugin's code (manifest read + signature inspection only):
/// the SDK-version (from the manifest) must be compatible, and the package must satisfy the active
/// <see cref="PluginTrustPolicy"/> (a curated host requires a pinned signature; the dev host is
/// permissive). The plugin actually loads on next startup via <see cref="PluginLoader"/>, which
/// re-checks everything.
/// </para>
/// </summary>
public static class PluginInstaller
{
    /// <summary>Installs the plugin whose main assembly is <paramref name="sourceDllPath"/> into
    /// <paramref name="pluginsRoot"/>, gated by <paramref name="policy"/> + <paramref name="inspector"/>.
    /// Never throws — failures come back as <see cref="PluginInstallResult.Success"/> = false.</summary>
    public static PluginInstallResult InstallFromDll(
        string sourceDllPath,
        string pluginsRoot,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector)
    {
        try
        {
            if (!File.Exists(sourceDllPath))
                return new(false, $"File not found: {sourceDllPath}");
            if (!sourceDllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return new(false, "Pick the plugin's main .dll (the assembly that contains its IStrategyPlugin).");

            var dllName = Path.GetFileNameWithoutExtension(sourceDllPath);
            var sourceDir = Path.GetDirectoryName(sourceDllPath)!;

            PluginManifest? manifest;
            try { manifest = PluginManifest.TryRead(sourceDir); }
            catch (Exception ex) { return new(false, $"Invalid plugin.json: {ex.Message}"); }

            if (manifest is not null && !PluginLoader.IsCompatible(manifest.TargetSdkVersion, SdkInfo.Version))
                return new(false,
                    $"Plugin targets DaxAlgo.Sdk {manifest.TargetSdkVersion}, incompatible with this host's SDK {SdkInfo.Version}.");

            var signature = policy.RequireSignature ? inspector.Inspect(sourceDllPath) : PluginSignature.Unsigned;
            if (!policy.Allows(signature, manifest is not null, out var reason))
                return new(false, $"Rejected by the plugin trust policy: {reason}.");

            var targetDir = Path.Combine(pluginsRoot, dllName);
            Directory.CreateDirectory(targetDir);
            File.Copy(sourceDllPath, Path.Combine(targetDir, dllName + ".dll"), overwrite: true);
            CopyIfExists(Path.Combine(sourceDir, PluginManifest.FileName), Path.Combine(targetDir, PluginManifest.FileName));
            CopyIfExists(Path.Combine(sourceDir, dllName + ".pdb"), Path.Combine(targetDir, dllName + ".pdb"));

            var display = manifest?.Name ?? dllName;
            return new(true, $"Installed '{display}'. Restart the app to activate it.", targetDir);
        }
        catch (Exception ex)
        {
            return new(false, $"Install failed: {ex.Message}");
        }
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
    }
}
