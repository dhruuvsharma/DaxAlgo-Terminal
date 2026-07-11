using System.IO;
using System.Reflection;
using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>Metadata about a plugin that was successfully discovered and registered.
/// <paramref name="RegisteredServices"/> is what it contributed to DI (attribution — every plugin
/// registration is attributable to the plugin that made it); <paramref name="Scan"/> carries the IL
/// scan's disclosed capabilities (file / network I/O), which loaded fine but the user should see.</summary>
public sealed record LoadedPlugin(
    string Name,
    string TargetSdkVersion,
    string AssemblyPath,
    IReadOnlyList<string>? RegisteredServices = null,
    PluginScanReport? Scan = null,
    bool Unsigned = false);

/// <summary>
/// Discovers and loads strategy plugins. A plugin is a folder under the plugins root containing a
/// <c>&lt;foldername&gt;.dll</c> that exposes one public parameterless <see cref="IStrategyPlugin"/>.
/// Each plugin loads in its own collectible <see cref="PluginLoadContext"/> (host contract assemblies
/// shared with the default context). A plugin's declared <see cref="IStrategyPlugin.TargetSdkVersion"/>
/// must be compatible with the host SDK (<see cref="SdkInfo.Version"/>) or it is rejected. One bad
/// plugin never blocks the host — per-plugin failures are classified into a
/// <see cref="PluginLoadReport"/> (and optionally reported via <c>onError</c>) and skipped.
/// <para>
/// With a <see cref="PluginStateStore"/>, the loader also honours persisted lifecycle state BEFORE
/// any code loads: pending uninstalls are deleted, user-disabled and quarantined plugins are
/// skipped, and a plugin that faults during load/registration is auto-quarantined so a
/// crash-looping plugin runs once, not at every startup.
/// </para>
/// <para>
/// The <see cref="RegisterFromAssembly"/> path (discovery + version check + Register) is separated
/// from file/ALC loading so it is unit-testable directly against an already-loaded assembly.
/// </para>
/// </summary>
public static class PluginLoader
{
    // A collectible AssemblyLoadContext starts UNLOADING the moment the GC collects the context
    // OBJECT — DI references to the plugin's types keep its memory alive but not the context itself,
    // leaving it stuck in the "unloading" state where any later demand-load of a plugin-private
    // dependency throws InvalidOperationException ("AssemblyLoadContext is unloading or was already
    // unloaded"). That's exactly what broke the Helix-based 3D strategy windows: HelixToolkit.Wpf
    // only loads the first time such a window opens, by which point the unreferenced context had
    // been finalized. There is no unload/hot-reload path today, so every context created here is
    // rooted for the life of the host.
    private static readonly List<PluginLoadContext> s_keepAlive = [];

    /// <summary>Scans <paramref name="pluginsRoot"/> and registers each plugin into
    /// <paramref name="services"/> using the <see cref="PluginTrustPolicy.Permissive"/> policy (the
    /// open-core dev flow — unsigned local plugins load, signatures aren't inspected). A missing
    /// directory or no plugins is a no-op.</summary>
    public static IReadOnlyList<LoadedPlugin> LoadInto(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        Action<string, Exception>? onError = null) =>
        LoadWithReport(services, pluginsRoot, hostSdkVersion, PluginTrustPolicy.Permissive, DefaultInspector,
            state: null, onError: onError).Loaded;

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
        Action<string, Exception>? onError = null) =>
        LoadWithReport(services, pluginsRoot, hostSdkVersion, policy, inspector, state: null, onError: onError).Loaded;

    /// <summary>Permissive-policy scan that returns the full <see cref="PluginLoadReport"/> and honours
    /// persisted lifecycle <paramref name="state"/>.</summary>
    public static PluginLoadReport LoadWithReport(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        PluginStateStore? state = null,
        Action<string, Exception>? onError = null) =>
        LoadWithReport(services, pluginsRoot, hostSdkVersion, PluginTrustPolicy.Permissive, DefaultInspector,
            state, onError: onError);

    /// <summary>Scan under a configured <paramref name="policy"/> and <paramref name="scanMode"/> with
    /// the default signature inspector (real Authenticode on Windows) — the shells' entry point.</summary>
    public static PluginLoadReport LoadWithReport(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        PluginTrustPolicy policy,
        PluginStateStore? state = null,
        PluginScanMode scanMode = PluginScanMode.Enforce,
        IPluginConsentPrompt? consent = null,
        Action<string, Exception>? onError = null) =>
        LoadWithReport(services, pluginsRoot, hostSdkVersion, policy, DefaultInspector, state, scanMode, consent, onError);

    /// <summary>Core scan: registers every loadable plugin and classifies every one that did NOT load
    /// (disabled / quarantined / trust-rejected / SDK-incompatible / bad manifest / faulted) so the
    /// host can surface problems instead of plugins silently vanishing from the catalog. When
    /// <paramref name="state"/> is provided, pending uninstalls are applied first, disabled and
    /// quarantined plugins are skipped pre-load, and a load/registration fault auto-quarantines.</summary>
    public static PluginLoadReport LoadWithReport(
        IServiceCollection services,
        string pluginsRoot,
        string hostSdkVersion,
        PluginTrustPolicy policy,
        IPluginSignatureInspector inspector,
        PluginStateStore? state = null,
        PluginScanMode scanMode = PluginScanMode.Enforce,
        IPluginConsentPrompt? consent = null,
        Action<string, Exception>? onError = null)
    {
        var loaded = new List<LoadedPlugin>();
        var problems = new List<PluginLoadProblem>();
        if (!Directory.Exists(pluginsRoot)) return new PluginLoadReport(loaded, problems);

        if (state is not null) ApplyPendingUninstalls(pluginsRoot, state);

        // The build pins the hashes of the plugins it shipped; the kill-list withdraws bad builds.
        // Both are read once per scan and consulted before any plugin code runs.
        var pinned = PluginTrustedHashes.Load(pluginsRoot);
        var revoked = PluginRevocationList.Load(pluginsRoot);

        foreach (var dll in EnumeratePluginAssemblies(pluginsRoot))
        {
            var folder = Path.GetFileNameWithoutExtension(dll);

            // Persisted lifecycle gates — all decided BEFORE any plugin code loads.
            if (state is not null)
            {
                if (state.PendingUninstalls.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    // Deletion failed above (external lock); never load a plugin marked for removal.
                    problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Disabled,
                        "Uninstall pending — the folder will be removed on a future start."));
                    continue;
                }
                if (state.IsDisabled(folder))
                {
                    problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Disabled,
                        "Disabled in the Plugin Manager."));
                    continue;
                }
                if (state.QuarantineFor(folder) is { } quarantine)
                {
                    problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Quarantined,
                        $"Quarantined {quarantine.QuarantinedUtc:u} — {quarantine.Reason}"));
                    continue;
                }
            }

            try
            {
                // ── Integrity / trust gates (all decided BEFORE loading any code) ─────────────────
                var pluginDir = Path.GetDirectoryName(dll)!;
                var manifest = PluginManifest.TryRead(pluginDir);
                var hash = PluginIntegrity.Sha256(dll);

                // 1. Was a plugin the BUILD shipped rewritten since? Checked in every mode — a modified
                //    first-party assembly is never acceptable, permissive dev build or not.
                var pin = pinned.Verify(folder, pluginDir, out var pinDetail);
                if (pin == PluginPinResult.Tampered)
                    throw new PluginTamperedException(dll, pinDetail ?? "the plugin folder does not match the shipped build");

                // 2. Was this exact build (or this plugin) withdrawn?
                if (revoked.IsRevoked(hash, manifest?.Id, out var revokedReason))
                    throw new PluginRevokedException(dll, revokedReason!);

                // 3. Was a plugin the USER installed rewritten since they installed it?
                if (pin != PluginPinResult.Match
                    && state?.InstalledHash(folder) is { Length: > 0 } installedHash
                    && !string.Equals(installedHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new PluginTamperedException(dll,
                        "the assembly has changed since it was installed — something rewrote it outside the app");

                // 4. Static IL policy scan. Runs BEFORE the trust/consent decision, so a plugin
                //    carrying Block-level code is refused outright and the user is never asked to
                //    consent to it — and so the consent dialog can show what the plugin reaches for.
                //    Still no code loaded: the assembly is read as DATA.
                var scan = scanMode == PluginScanMode.Off
                    ? PluginScanReport.Clean
                    : PluginPolicyScanner.Scan(pluginDir, manifest?.Permissions);
                if (scan.Verdict == PluginScanSeverity.Block && scanMode == PluginScanMode.Enforce)
                    throw new PluginBlockedException(dll, scan);

                // 5. Trust. A pinned first-party plugin IS the trust anchor (that's what hash-pinning
                //    buys us: a shipped catalogue that loads under Curated without a code-signing
                //    certificate); everything else must satisfy the policy, or be one the user has
                //    explicitly consented to run.
                var unsigned = false;
                if (pin != PluginPinResult.Match)
                {
                    // Inspect the signature only when the policy actually needs it (the permissive dev
                    // flow skips Authenticode entirely).
                    var signature = policy.RequireSignature ? inspector.Inspect(dll) : PluginSignature.Unsigned;
                    if (!policy.Allows(signature, manifest is not null, out var reason))
                    {
                        if (!Consented(folder, hash, dll, manifest, scan, state, consent))
                            throw new PluginRejectedException(dll, reason!);
                    }
                    // Neither ours-by-hash nor signed-by-a-trusted-publisher: it runs on the user's
                    // say-so (or a permissive dev build), so it wears the DEV / UNSIGNED badge.
                    unsigned = !signature.IsSigned || !signature.IsValid;
                }

                // ── Load + register ──────────────────────────────────────────────────────────────
                var ctx = new PluginLoadContext(dll);
                lock (s_keepAlive) s_keepAlive.Add(ctx);
                var asm = ctx.LoadFromAssemblyPath(dll);
                if (RegisterFromAssembly(asm, services, hostSdkVersion) is { } meta)
                    loaded.Add(meta with { Scan = scan, Unsigned = unsigned });
            }
            catch (PluginBlockedException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.BlockedByScan, ex.Reason));
                state?.Quarantine(folder, ex.Reason);
                onError?.Invoke(dll, ex);
            }
            catch (PluginTamperedException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Tampered, ex.Reason));
                state?.Quarantine(folder, ex.Reason);
                onError?.Invoke(dll, ex);
            }
            catch (PluginRevokedException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Revoked, ex.Reason));
                state?.Quarantine(folder, ex.Reason);
                onError?.Invoke(dll, ex);
            }
            catch (PluginRejectedException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.RejectedByTrust, ex.Reason));
                onError?.Invoke(dll, ex);
            }
            catch (PluginPolicyViolationException ex)
            {
                // It registered nothing (the guard staged and discarded), but it TRIED to take over a
                // host service — quarantine so it doesn't get a second attempt on the next start.
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.PolicyViolation, ex.Reason));
                state?.Quarantine(folder, ex.Reason);
                onError?.Invoke(dll, ex);
            }
            catch (PluginIncompatibleException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.IncompatibleSdk,
                    $"Targets DaxAlgo.Sdk {ex.PluginVersion}; this host has {ex.HostVersion}."));
                onError?.Invoke(dll, ex);
            }
            catch (InvalidDataException ex)
            {
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.ManifestInvalid, ex.Message));
                onError?.Invoke(dll, ex);
            }
            catch (Exception ex)
            {
                var reason = Flatten(ex);
                problems.Add(new PluginLoadProblem(folder, dll, PluginLoadOutcome.Faulted, reason));
                // Run once, not at every startup: a load/registration crash is persisted so the next
                // start skips the plugin until the user re-enables it (or installs a fixed version).
                state?.Quarantine(folder, reason);
                onError?.Invoke(dll, ex);
            }
        }
        return new PluginLoadReport(loaded, problems);
    }

    /// <summary>Deletes plugin folders the user uninstalled while their assemblies were file-locked
    /// by a live load context. Runs before any load, so the files are free. A folder that still
    /// can't be deleted keeps its mark (and is skipped by the scan).</summary>
    private static void ApplyPendingUninstalls(string pluginsRoot, PluginStateStore state)
    {
        foreach (var plugin in state.PendingUninstalls)
        {
            // Folder names only — never a path. Defensive: a corrupt/hand-edited state file must not
            // become a delete-anything primitive.
            if (plugin.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                || plugin is "." or "..")
            {
                state.ClearPendingUninstall(plugin);
                continue;
            }

            var dir = Path.Combine(pluginsRoot, plugin);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                state.ClearPendingUninstall(plugin);
            }
            catch
            {
                // Still locked by something external — keep the mark; the scan skips it either way.
            }
        }
    }

    /// <summary>The escape hatch for a plugin the host cannot vouch for: has the user already said yes
    /// to THIS EXACT build, or will they now? A remembered consent is keyed by sha256, so an update
    /// re-asks rather than inheriting its predecessor's trust. With no prompt (headless: the CLI,
    /// tests) the answer is always no — nothing is trusted merely because there was nobody to ask.</summary>
    private static bool Consented(
        string folder,
        string hash,
        string dll,
        PluginManifest? manifest,
        PluginScanReport scan,
        PluginStateStore? state,
        IPluginConsentPrompt? consent)
    {
        if (state?.HasConsent(folder, hash) == true) return true;
        if (consent is null) return false;

        var request = new PluginConsentRequest(
            folder, manifest?.Name ?? folder, manifest?.Publisher, dll, hash, scan);
        if (!consent.RequestConsent(request)) return false;

        state?.GrantConsent(folder, hash);
        return true;
    }

    /// <summary>The default signature inspector: real Authenticode on Windows, null (always unsigned)
    /// elsewhere. Only consulted when a policy requires signatures.</summary>
    private static IPluginSignatureInspector DefaultInspector =>
        OperatingSystem.IsWindows() ? new AuthenticodeSignatureInspector() : new NullSignatureInspector();

    /// <summary>Finds the single public <see cref="IStrategyPlugin"/> in <paramref name="assembly"/>,
    /// checks its version against <paramref name="hostSdkVersion"/>, and invokes
    /// <see cref="IStrategyPlugin.Register"/> against a <see cref="GuardedServiceCollection"/> — the
    /// plugin's registrations are staged and only committed to <paramref name="services"/> once
    /// <c>Register</c> returns cleanly, so a plugin that tries to replace a host service
    /// (<c>ICredentialStore</c>, <c>IBrokerSelector</c>, …) contributes nothing at all. Returns
    /// <c>null</c> when the assembly contains no plugin type. Throws
    /// <see cref="PluginIncompatibleException"/> on a version mismatch and
    /// <see cref="PluginPolicyViolationException"/> on a forbidden registration.</summary>
    public static LoadedPlugin? RegisterFromAssembly(Assembly assembly, IServiceCollection services, string hostSdkVersion)
    {
        var pluginType = assembly.GetExportedTypes().FirstOrDefault(t =>
            typeof(IStrategyPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });
        if (pluginType is null) return null;

        var plugin = (IStrategyPlugin)Activator.CreateInstance(pluginType)!;
        if (!IsCompatible(plugin.TargetSdkVersion, hostSdkVersion))
            throw new PluginIncompatibleException(plugin.Name, plugin.TargetSdkVersion, hostSdkVersion);

        var path = SafeLocation(assembly);
        var guarded = new GuardedServiceCollection(services, plugin.Name);
        plugin.Register(new PluginRegistrar(guarded, new PluginContext(plugin.Name, path, plugin.TargetSdkVersion)));
        var registered = guarded.Commit();
        return new LoadedPlugin(plugin.Name, plugin.TargetSdkVersion, path, registered);
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

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null && parts.Count < 4; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
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

/// <summary>Thrown when the static IL scan finds a Block-level capability
/// (<see cref="PluginPolicyScanner"/>). The plugin's code is NOT loaded — the scan reads the assembly
/// as data.</summary>
public sealed class PluginBlockedException(string assemblyPath, PluginScanReport scan)
    : Exception($"Plugin '{assemblyPath}' blocked by the policy scan: {scan.Summary}.")
{
    public string AssemblyPath { get; } = assemblyPath;
    public PluginScanReport Scan { get; } = scan;
    public string Reason { get; } = scan.Summary;
}

/// <summary>Thrown when a plugin's assemblies no longer hash to what the build shipped
/// (<see cref="PluginTrustedHashes"/>) or to what the user installed. Something rewrote the plugin
/// outside the app — it is quarantined, in every trust mode.</summary>
public sealed class PluginTamperedException(string assemblyPath, string reason)
    : Exception($"Plugin '{assemblyPath}' failed its integrity check: {reason}.")
{
    public string AssemblyPath { get; } = assemblyPath;
    public string Reason { get; } = reason;
}

/// <summary>Thrown when a plugin build is on the local kill-list
/// (<see cref="PluginRevocationList"/>).</summary>
public sealed class PluginRevokedException(string assemblyPath, string reason)
    : Exception($"Plugin '{assemblyPath}' is revoked: {reason}.")
{
    public string AssemblyPath { get; } = assemblyPath;
    public string Reason { get; } = reason;
}
