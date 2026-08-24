using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DaxAlgo.Package;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Configuration;

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
    PluginStateStore? State = null)
{
    private readonly List<LoadedPlugin> _authored = [];
    private readonly List<LoadedPlugin> _runtimeInstalled = [];

    /// <summary>Plugins compiled and registered THIS session by the AI Strategy Builder — they were never
    /// seen by the startup loader, but they are running, so the Plugin Manager must show them. On the next
    /// start they are ordinary plugins (they were written to the plugins folder) and appear in
    /// <see cref="LoadedPlugins"/> instead.</summary>
    public IReadOnlyList<LoadedPlugin> AuthoredThisSession
    {
        get { lock (_authored) return [.. _authored]; }
    }

    /// <summary>Records a strategy the user just authored. Replaces an earlier one with the same assembly
    /// path — regenerating a strategy updates its row rather than stacking duplicates.</summary>
    public void AddAuthored(LoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_authored)
        {
            _authored.RemoveAll(p => string.Equals(p.AssemblyPath, plugin.AssemblyPath, StringComparison.OrdinalIgnoreCase));
            _authored.Add(plugin);
        }
    }

    /// <summary>
    /// Verified artifacts installed and activated after startup. They were not part of the immutable
    /// startup report, but the Plugin Manager must still show their live lifecycle state.
    /// </summary>
    public IReadOnlyList<LoadedPlugin> RuntimeInstalledThisSession
    {
        get { lock (_runtimeInstalled) return [.. _runtimeInstalled]; }
    }

    /// <summary>Records or replaces one artifact activated after startup.</summary>
    public void AddRuntimeInstalled(LoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_runtimeInstalled)
        {
            _runtimeInstalled.RemoveAll(p =>
                string.Equals(p.AssemblyPath, plugin.AssemblyPath, StringComparison.OrdinalIgnoreCase));
            _runtimeInstalled.Add(plugin);
        }
    }

    /// <summary>Full type names of the <c>ITradingStrategy</c> implementations contributed by plugins
    /// that loaded UNSIGNED (neither shipped-by-us nor from a pinned publisher). The shell maps these to
    /// catalog cards so an unsigned plugin's strategies wear the DEV badge, mirroring the Plugin
    /// Manager. Empty in a fully-curated install.</summary>
    public IReadOnlySet<string> UnsignedStrategyTypeNames { get; } =
        LoadedPlugins
            .Where(p => p.Unsigned)
            .SelectMany(p => p.StrategyImplementationTypes ?? [])
            .ToHashSet(StringComparer.Ordinal);
}

/// <summary>The outcome of an install attempt.</summary>
public sealed record PluginInstallResult(bool Success, string Message, string? InstalledPath = null);

/// <summary>
/// Installs an artifact into the host's plugins folder — from a raw main assembly
/// (<see cref="InstallFromDll"/>: the dev drop-in path; dll + optional plugin.json/.pdb/.deps.json
/// beside it) or from a verified package (<see cref="InstallFromArtifact"/>: digest-checked, carries
/// private dependencies). Both land on <c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c> (the loader's
/// folder convention) through the same manifest/SDK/trust gates.
///
/// <para>There are exactly two artifact extensions — <c>.daxalgostrategy</c> and
/// <c>.daxalgovisualizer</c>. The legacy <c>.daxplugin</c> path was removed on 2026-08-24.</para>
/// <para>
/// Validation runs WITHOUT executing the plugin's code (manifest read + signature inspection only).
/// The plugin actually loads on next startup via <see cref="PluginLoader"/>, which re-checks
/// everything.
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
        PluginStateStore? state = null,
        PluginScanMode scanMode = PluginScanMode.Enforce)
    {
        try
        {
            if (!File.Exists(sourceDllPath))
                return new(false, $"File not found: {sourceDllPath}");
            if (!sourceDllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return new(false, "Pick the plugin's main .dll (the assembly that contains its IStrategyPlugin).");

            return InstallValidatedFolder(
                Path.GetDirectoryName(sourceDllPath)!,
                Path.GetFileNameWithoutExtension(sourceDllPath),
                pluginsRoot, policy, inspector, state, scanMode,
                copyAllFiles: false);
        }
        catch (Exception ex)
        {
            return new(false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Installs a <c>.daxalgostrategy</c> or <c>.daxalgovisualizer</c> — the only two artifact formats
    /// the terminal accepts. <see cref="DaxPackage.Read"/> verifies the manifest, every payload digest,
    /// and that no undeclared entry is hiding in the archive, all before a byte reaches the plugins
    /// folder. Verified payloads are staged to a temp folder and then go through the same
    /// manifest / SDK / trust / IL-scan gates as any other install, copied WHOLE so an artifact can
    /// carry its own private dependencies. Never throws.
    /// </summary>
    public static PluginInstallResult InstallFromArtifact(
        string artifactPath,
        string pluginsRoot,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        PluginStateStore? state = null,
        PluginScanMode scanMode = PluginScanMode.Enforce)
    {
        try
        {
            if (!File.Exists(artifactPath))
                return new(false, $"File not found: {artifactPath}");
            if (!DaxPackage.IsAccepted(artifactPath, out var rejection))
                return new(false, rejection);

            var contents = DaxPackage.Read(artifactPath);

            // The entry assembly names the plugin folder, keeping the loader's
            // plugins/<Name>/<Name>.dll convention. A source-only package is a perfectly valid package
            // and simply not an installable one — saying that plainly beats a missing-dll error raised
            // three frames further down.
            var assembly = contents.Manifest.Payloads.FirstOrDefault(p => p.Role == DaxPayloadRole.Assembly);
            if (assembly is null)
                return new(false,
                    $"'{contents.Manifest.DisplayName}' carries no compiled assembly — it is a source-only "
                    + "package and has to be built before it can be installed.");

            var assemblyName = Path.GetFileNameWithoutExtension(assembly.Path);
            if (string.IsNullOrWhiteSpace(assemblyName))
                return new(false, "The package's assembly payload has no file name.");

            var staging = Path.Combine(Path.GetTempPath(), "daxalgo-install-" + Guid.NewGuid().ToString("N"));
            try
            {
                Stage(contents, staging);
                return InstallValidatedFolder(
                    staging, assemblyName, pluginsRoot, policy, inspector, state, scanMode,
                    copyAllFiles: true);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (DaxPackageException ex)
        {
            return new(false, $"Rejected: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new(false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>Writes verified payloads into <paramref name="root"/>, flattening the manifest's
    /// <c>payload/</c> prefix. The paths were already normalised when the package was read; the
    /// containment check here is the second lock on the same door, placed at the moment bytes actually
    /// reach the disk.</summary>
    private static void Stage(DaxPackageContents contents, string root)
    {
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);

        foreach (var (path, bytes) in contents.Payloads)
        {
            var relative = path.StartsWith("payload/", StringComparison.Ordinal)
                ? path["payload/".Length..]
                : path;
            var destination = Path.GetFullPath(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new DaxPackageException(
                    DaxPackageError.UnsafePath, $"Payload '{path}' escapes the staging folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
        }
    }

    /// <summary>Removes the plugin folder <paramref name="pluginFolderName"/> under
    /// <paramref name="pluginsRoot"/>. A plugin that loaded this session has its assembly file-locked
    /// by its rooted load context — deletion then fails, so it is marked pending in
    /// <paramref name="state"/> and <c>PluginLoader.LoadWithReport</c> deletes it on the next
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
            state?.ClearInstalledHash(pluginFolderName);
            // Consent was given to a plugin that no longer exists. A future reinstall must ask again.
            state?.ClearConsent(pluginFolderName);
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

    /// <summary>The shared gate + copy core: manifest read, SDK-version check, trust policy on the
    /// main assembly, version-aware replace message, then the copy — the known file set for raw-DLL
    /// installs, or the whole folder for verified packages (private deps included).</summary>
    private static PluginInstallResult InstallValidatedFolder(
        string sourceDir,
        string dllName,
        string pluginsRoot,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        PluginStateStore? state,
        PluginScanMode scanMode,
        bool copyAllFiles)
    {
        PluginManifest? manifest;
        try { manifest = PluginManifest.TryRead(sourceDir); }
        catch (Exception ex) { return new(false, $"Invalid plugin.json: {ex.Message}"); }

        if (manifest is not null && !PluginLoader.IsCompatible(manifest.TargetSdkVersion, SdkInfo.Version))
            return new(false,
                $"Plugin targets DaxAlgo.Sdk {manifest.TargetSdkVersion}, incompatible with this host's SDK {SdkInfo.Version}.");

        var mainDll = Path.Combine(sourceDir, dllName + ".dll");
        if (!File.Exists(mainDll))
            return new(false, $"The package does not contain {dllName}.dll.");

        var signature = policy.RequireSignature ? inspector.Inspect(mainDll) : PluginSignature.Unsigned;
        if (!policy.Allows(signature, manifest is not null, out var reason))
            return new(false, $"Rejected by the plugin trust policy: {reason}.");

        // Static IL scan at install time too, so a plugin carrying Block-level capabilities never even
        // lands in the plugins folder (the loader would refuse it at the next start anyway — better to
        // say so now, while the user is looking at the install dialog).
        if (scanMode != PluginScanMode.Off)
        {
            var scan = PluginPolicyScanner.Scan(sourceDir, manifest?.Permissions);
            if (scan.Verdict == PluginScanSeverity.Block && scanMode == PluginScanMode.Enforce)
                return new(false, $"Blocked by the policy scan: {scan.Summary}.");
        }

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
        if (copyAllFiles)
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
        else
        {
            File.Copy(mainDll, Path.Combine(targetDir, dllName + ".dll"), overwrite: true);
            CopyIfExists(Path.Combine(sourceDir, PluginManifest.FileName), Path.Combine(targetDir, PluginManifest.FileName));
            CopyIfExists(Path.Combine(sourceDir, dllName + ".pdb"), Path.Combine(targetDir, dllName + ".pdb"));
            CopyIfExists(Path.Combine(sourceDir, dllName + ".deps.json"), Path.Combine(targetDir, dllName + ".deps.json"));
        }

        // A new package gets a clean slate: past faults and pending removals no longer apply.
        state?.ClearQuarantine(dllName);
        state?.ClearPendingUninstall(dllName);
        // Record what we installed, so the loader can tell on every start whether anything rewrote the
        // assembly afterwards (tamper detection for plugins the build doesn't pin).
        state?.SetInstalledHash(dllName, PluginIntegrity.Sha256(Path.Combine(targetDir, dllName + ".dll")));

        var display = manifest?.Name ?? dllName;
        var stillDisabled = state?.IsDisabled(dllName) == true
            ? " Note: it is currently disabled — re-enable it in the Plugin Manager."
            : string.Empty;
        var verb = DescribeReplace(existingVersion, manifest?.Version);
        return new(true, $"{verb} '{display}'. Restart the app to activate it.{stillDisabled}", targetDir);
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
