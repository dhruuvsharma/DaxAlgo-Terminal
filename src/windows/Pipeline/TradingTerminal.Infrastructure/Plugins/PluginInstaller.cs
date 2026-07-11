using System;
using System.Collections.Generic;
using System.IO;
using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>What the host knows about the plugin subsystem at runtime — surfaced to the Plugin
/// Manager UI. <see cref="LoadedPlugins"/> is captured at startup by the composition root;
/// <see cref="Report"/> additionally carries every plugin that did NOT load and why, and
/// <see cref="State"/> is the persisted disable/quarantine/uninstall store the manager mutates.</summary>
public sealed record PluginHostContext(
    string PluginsRoot,
    PluginTrustPolicy TrustPolicy,
    IReadOnlyList<LoadedPlugin> LoadedPlugins,
    PluginLoadReport? Report = null,
    PluginStateStore? State = null);

/// <summary>The outcome of an install attempt.</summary>
public sealed record PluginInstallResult(bool Success, string Message, string? InstalledPath = null);

/// <summary>
/// Installs a strategy-plugin package into the host's plugins folder. A package is the plugin's main
/// assembly (<c>&lt;Name&gt;.dll</c>) plus an optional <c>plugin.json</c>, <c>.pdb</c> and
/// <c>.deps.json</c> beside it; install copies them to <c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c>
/// (the loader's folder convention).
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
    /// Installing over an existing version replaces it (the message says update / reinstall /
    /// downgrade), and clears any quarantine or pending-uninstall mark in <paramref name="state"/> —
    /// a freshly installed package deserves a fresh chance (a user *disable* is respected and kept).
    /// Never throws — failures come back as <see cref="PluginInstallResult.Success"/> = false.</summary>
    public static PluginInstallResult InstallFromDll(
        string sourceDllPath,
        string pluginsRoot,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        PluginStateStore? state = null)
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
            // Version-aware message: compare what's already installed (manifest-declared) with the
            // incoming package so an update / reinstall / downgrade is called out, not silent.
            string? existingVersion = null;
            if (Directory.Exists(targetDir))
            {
                try { existingVersion = PluginManifest.TryRead(targetDir)?.Version; }
                catch { /* broken existing manifest — treat as unknown version */ }
            }

            Directory.CreateDirectory(targetDir);
            File.Copy(sourceDllPath, Path.Combine(targetDir, dllName + ".dll"), overwrite: true);
            CopyIfExists(Path.Combine(sourceDir, PluginManifest.FileName), Path.Combine(targetDir, PluginManifest.FileName));
            CopyIfExists(Path.Combine(sourceDir, dllName + ".pdb"), Path.Combine(targetDir, dllName + ".pdb"));
            CopyIfExists(Path.Combine(sourceDir, dllName + ".deps.json"), Path.Combine(targetDir, dllName + ".deps.json"));

            // A new package gets a clean slate: past faults and pending removals no longer apply.
            state?.ClearQuarantine(dllName);
            state?.ClearPendingUninstall(dllName);

            var display = manifest?.Name ?? dllName;
            var stillDisabled = state?.IsDisabled(dllName) == true
                ? " Note: it is currently disabled — re-enable it in the Plugin Manager."
                : string.Empty;
            var verb = DescribeReplace(existingVersion, manifest?.Version);
            return new(true, $"{verb} '{display}'. Restart the app to activate it.{stillDisabled}", targetDir);
        }
        catch (Exception ex)
        {
            return new(false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>Removes the plugin folder <paramref name="pluginFolderName"/> under
    /// <paramref name="pluginsRoot"/>. A plugin that loaded this session has its assembly file-locked
    /// by its rooted load context — deletion then fails, so it is marked pending in
    /// <paramref name="state"/> and <see cref="PluginLoader.LoadWithReport"/> deletes it on the next
    /// start, before anything loads. Never throws.</summary>
    public static PluginInstallResult Uninstall(
        string pluginsRoot,
        string pluginFolderName,
        PluginStateStore? state = null)
    {
        // Folder names only — never a path fragment.
        if (string.IsNullOrWhiteSpace(pluginFolderName)
            || pluginFolderName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || pluginFolderName is "." or "..")
            return new(false, $"Not a plugin folder name: '{pluginFolderName}'.");

        var dir = Path.Combine(pluginsRoot, pluginFolderName);
        if (!Directory.Exists(dir))
        {
            state?.ClearPendingUninstall(pluginFolderName);
            return new(true, $"'{pluginFolderName}' is not installed.");
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            // Folder is gone — its persisted state no longer applies.
            state?.ClearQuarantine(pluginFolderName);
            state?.ClearPendingUninstall(pluginFolderName);
            state?.SetDisabled(pluginFolderName, false);
            return new(true, $"Uninstalled '{pluginFolderName}'.");
        }
        catch (Exception) when (state is not null)
        {
            state.MarkPendingUninstall(pluginFolderName);
            return new(true, $"'{pluginFolderName}' is in use by this session — it will be removed when the app next starts.");
        }
        catch (Exception ex)
        {
            return new(false, $"Uninstall failed: {ex.Message}");
        }
    }

    private static string DescribeReplace(string? existingVersion, string? newVersion)
    {
        if (existingVersion is null) return "Installed";
        if (newVersion is null || existingVersion == newVersion) return "Reinstalled";
        var downgrade = Version.TryParse(Core(existingVersion), out var oldV)
                        && Version.TryParse(Core(newVersion), out var newV)
                        && newV < oldV;
        return downgrade
            ? $"DOWNGRADED ({existingVersion} → {newVersion})"
            : $"Updated ({existingVersion} → {newVersion})";

        static string Core(string v) => v.Split('-', '+')[0];
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
    }
}
