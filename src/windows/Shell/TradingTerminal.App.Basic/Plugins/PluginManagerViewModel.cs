using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using TradingTerminal.UI;

namespace TradingTerminal.App.Plugins;

/// <summary>One row in the plugins list — a loaded plugin OR one that failed/was skipped, with the
/// lifecycle actions that currently apply to it. Rows are immutable; any action rebuilds the list
/// from the load report + the (mutated) persisted state.</summary>
public sealed record PluginRow(
    string Key,
    string Name,
    string Detail,
    string StatusLabel,
    bool IsProblem,
    bool CanEnable,
    bool CanDisable,
    bool CanUninstall);

/// <summary>
/// Plugin Manager view-model (View → "Manage strategy plugins…"). Two tabs:
/// <list type="bullet">
/// <item><b>Installed</b> — every plugin the loader SAW: loaded ones and, crucially, every one that did
/// not load with its classified reason (failed / quarantined / trust-rejected / SDK-incompatible /
/// disabled), plus the lifecycle actions (install, enable/disable, re-enable, uninstall).</item>
/// <item><b>Catalog</b> — the signed marketplace feed (issue #25): browse/search, Install, and Update
/// through the same download → checksum → trust → scan → installer gate chain. Only shown when a feed is
/// configured; offline-first from the last-good cached index.</item>
/// </list>
/// Lifecycle changes are persisted in <see cref="PluginStateStore"/> and take effect on the next start (a
/// loaded plugin's assembly is file-locked by its rooted load context), so mutations raise the restart
/// banner.
/// </summary>
public sealed partial class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginHostContext _context;
    private readonly PluginStateStore? _state;
    private readonly PluginFeedClient _feed;
    private readonly IHttpClientFactory _httpFactory;
    private IReadOnlyList<PluginCatalogItem> _allCatalog = [];

    public PluginManagerViewModel(PluginHostContext context, PluginFeedClient feed, IHttpClientFactory httpFactory)
    {
        _context = context;
        _state = context.State;
        _feed = feed;
        _httpFactory = httpFactory;
        PluginsRoot = context.PluginsRoot;
        TrustPolicySummary = context.TrustPolicy.RequireSignature
            ? "Curated mode — only plugins signed by a trusted publisher will load."
            : "Permissive (developer mode) — unsigned local plugins load.";

        Rebuild();

        var problems = context.Report?.AttentionCount ?? 0;
        Status = Rows.Count == 0
            ? "No plugins installed yet. Install one below, then restart the app."
            : problems == 0
                ? $"{context.LoadedPlugins.Count} plugin(s) loaded."
                : $"{context.LoadedPlugins.Count} plugin(s) loaded — {problems} problem(s) need attention below.";

        // Show whatever the background refresh already cached; then check for a newer index (no-op /
        // instant when the feed is off or unreachable). Fire-and-forget on the UI context.
        RebuildCatalog();
        if (_feed.IsConfigured)
        {
            CatalogStatus = "Checking the marketplace…";
            _ = RefreshCatalogAsync();
        }
    }

    public string PluginsRoot { get; }
    public string TrustPolicySummary { get; }
    public ObservableCollection<PluginRow> Rows { get; } = new();

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True once any lifecycle change (enable/disable/uninstall/install) is pending a
    /// restart — drives the banner. Loaded plugins are file-locked, so nothing applies live.</summary>
    [ObservableProperty] private bool _restartRequired;

    // ── Catalog (marketplace feed) ──────────────────────────────────────────────────────────────────

    /// <summary>The filtered marketplace catalog bound by the Catalog tab.</summary>
    public ObservableCollection<PluginCatalogItem> CatalogItems { get; } = new();

    /// <summary>Whether a feed is configured at all — the Catalog tab is hidden otherwise.</summary>
    public bool FeedConfigured => _feed.IsConfigured;

    [ObservableProperty] private string _catalogSearch = string.Empty;
    [ObservableProperty] private string _catalogStatus = string.Empty;
    [ObservableProperty] private bool _catalogBusy;

    /// <summary>True when at least one installed plugin has a newer build in the feed — enables "Update all".</summary>
    [ObservableProperty] private bool _hasUpdates;

    partial void OnCatalogSearchChanged(string value) => ApplySearch();

    /// <summary>Pick a plugin package (.daxplugin, integrity-verified) or a raw main .dll (the dev
    /// drop-in), validate it against the active trust policy + SDK version, and copy it into the
    /// plugins folder. Activation is on next startup.</summary>
    [RelayCommand]
    private void InstallPlugin()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a plugin package (.daxplugin) or its main .dll",
            Filter = "Plugin package or assembly (*.daxplugin;*.dll)|*.daxplugin;*.dll",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        // Packages are sha256-verified and carry private deps; a raw .dll is the dev drop-in path.
        // Both go through the same manifest/SDK/trust gates.
        var result = dialog.FileName.EndsWith(DaxPluginPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? PluginInstaller.InstallFromPackage(
                dialog.FileName, _context.PluginsRoot, _context.TrustPolicy, Inspector(), _state)
            : PluginInstaller.InstallFromDll(
                dialog.FileName, _context.PluginsRoot, _context.TrustPolicy, Inspector(), _state);
        Status = result.Message;
        if (result.Success) RestartRequired = true;
        Rebuild();
        RebuildCatalog();
    }

    /// <summary>Re-enable a disabled or quarantined plugin — it loads again on the next start.</summary>
    [RelayCommand]
    private void Enable(PluginRow row)
    {
        if (_state is null) return;
        _state.SetDisabled(row.Key, false);
        _state.ClearQuarantine(row.Key);
        Status = $"'{row.Name}' will load when the app next starts.";
        RestartRequired = true;
        Rebuild();
    }

    /// <summary>Disable a plugin without removing it — skipped (before any of its code runs) from the
    /// next start until re-enabled.</summary>
    [RelayCommand]
    private void Disable(PluginRow row)
    {
        if (_state is null) return;
        _state.SetDisabled(row.Key, true);
        Status = $"'{row.Name}' is disabled — it will not load after the next restart.";
        RestartRequired = true;
        Rebuild();
    }

    /// <summary>Delete the plugin's folder. A plugin loaded this session is file-locked, so it is
    /// marked for removal and swept on the next start instead.</summary>
    [RelayCommand]
    private void Uninstall(PluginRow row)
    {
        var result = PluginInstaller.Uninstall(_context.PluginsRoot, row.Key, _state);
        Status = result.Message;
        if (result.Success) RestartRequired = true;
        Rebuild();
        RebuildCatalog();
    }

    /// <summary>Open the plugins folder in the file explorer so a developer can drop a package in by hand.</summary>
    [RelayCommand]
    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(_context.PluginsRoot);
        Process.Start(new ProcessStartInfo { FileName = _context.PluginsRoot, UseShellExecute = true });
    }

    /// <summary>Fetch the signed index (background, offline-first), sync revocations into the local
    /// kill-list, and rebuild the catalog against what's installed.</summary>
    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        if (!_feed.IsConfigured) { CatalogStatus = "No plugin feed is configured."; return; }

        CatalogBusy = true;
        try
        {
            var result = await _feed.RefreshAsync();
            if (result.Index is { } index)
                PluginRevocationSync.Apply(_context.PluginsRoot, index);
            RebuildCatalog();
            CatalogStatus = _feed.Current is null
                ? "The marketplace could not be reached and there is no saved catalog yet."
                : result.FromCache
                    ? $"Showing the last saved catalog — {CatalogItems.Count} plugin(s)."
                    : $"{_allCatalog.Count} plugin(s) available.";
        }
        finally { CatalogBusy = false; }
    }

    /// <summary>Install (or update to) the feed's latest build of a catalog plugin: download →
    /// checksum-verify against the signed index → the standard manifest/SDK/trust/IL-scan gates.</summary>
    [RelayCommand]
    private async Task InstallFromCatalogAsync(PluginCatalogItem? item)
    {
        if (item is null || CatalogBusy) return;

        CatalogBusy = true;
        try
        {
            CatalogStatus = $"Downloading '{item.Name}' {item.LatestVersion}…";
            var result = await PluginCatalogInstaller.InstallAsync(
                _httpFactory.CreateClient(PluginFeedServiceCollectionExtensions.FeedHttpClientName),
                item.Latest, _context.PluginsRoot, _context.TrustPolicy, Inspector(), _state);

            CatalogStatus = result.Message;
            Status = result.Message;
            if (result.Success) RestartRequired = true;
            Rebuild();
            RebuildCatalog();
        }
        finally { CatalogBusy = false; }
    }

    /// <summary>Update every installed plugin that has a newer build in the feed.</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (CatalogBusy) return;

        var updatable = PluginCatalog.Updatable(_allCatalog);
        if (updatable.Count == 0) { CatalogStatus = "Everything is up to date."; return; }

        CatalogBusy = true;
        try
        {
            var updated = 0;
            foreach (var item in updatable)
            {
                CatalogStatus = $"Updating '{item.Name}' → {item.LatestVersion}…";
                var result = await PluginCatalogInstaller.InstallAsync(
                    _httpFactory.CreateClient(PluginFeedServiceCollectionExtensions.FeedHttpClientName),
                    item.Latest, _context.PluginsRoot, _context.TrustPolicy, Inspector(), _state);
                if (result.Success) updated++;
            }
            if (updated > 0) RestartRequired = true;
            Rebuild();
            RebuildCatalog();
            CatalogStatus = $"Updated {updated} of {updatable.Count} plugin(s). Restart to apply.";
        }
        finally { CatalogBusy = false; }
    }

    /// <summary>Rows = loaded plugins + every problem from the startup report, each overlaid with the
    /// CURRENT persisted state (the user may have enabled/disabled/uninstalled since startup).</summary>
    private void Rebuild()
    {
        Rows.Clear();

        foreach (var plugin in _context.LoadedPlugins)
        {
            var key = string.IsNullOrEmpty(plugin.AssemblyPath)
                ? plugin.Name
                : Path.GetFileNameWithoutExtension(plugin.AssemblyPath);
            var pendingUninstall = IsPendingUninstall(key);
            var disabled = _state?.IsDisabled(key) == true;
            // A plugin that is neither shipped-by-us (hash-pinned) nor signed by a pinned publisher is
            // running on the user's say-so. Say so, permanently — not just at the consent prompt.
            var badge = plugin.Unsigned ? " · DEV (unsigned)" : string.Empty;
            var label = (pendingUninstall ? "Loaded — uninstalls on restart"
                : disabled ? "Loaded — disables on restart"
                : "Loaded") + badge;
            // Disclose what the IL scan saw the plugin reach for (file / network I/O). Block-level
            // capabilities never get here — those plugins don't load.
            var capabilities = (plugin.Scan?.Findings ?? [])
                .Select(f => f.Rule)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var detail = capabilities.Length == 0
                ? $"SDK {plugin.TargetSdkVersion}"
                : $"SDK {plugin.TargetSdkVersion} — uses {string.Join(", ", capabilities)}";
            Rows.Add(new PluginRow(key, plugin.Name, detail, label,
                IsProblem: false,
                CanEnable: disabled,
                CanDisable: !disabled && !pendingUninstall && _state is not null,
                CanUninstall: !pendingUninstall));
        }

        foreach (var problem in _context.Report?.Problems ?? [])
        {
            var key = problem.PluginFolderName;
            var pendingUninstall = IsPendingUninstall(key);
            var disabledNow = _state?.IsDisabled(key) == true;
            var quarantinedNow = _state?.QuarantineFor(key) is not null;

            // The startup outcome, overlaid with what the user has done since.
            string label;
            var isProblem = true;
            if (pendingUninstall) { label = "Uninstalls on restart"; isProblem = false; }
            else if (problem.Outcome is PluginLoadOutcome.Disabled or PluginLoadOutcome.Quarantined
                     && !disabledNow && !quarantinedNow) { label = "Re-enabled — loads on restart"; isProblem = false; }
            else
            {
                label = problem.Outcome switch
                {
                    PluginLoadOutcome.Disabled => "Disabled",
                    PluginLoadOutcome.Quarantined => "Quarantined",
                    PluginLoadOutcome.RejectedByTrust => "Blocked by trust policy",
                    PluginLoadOutcome.PolicyViolation => "Blocked — unsafe registration",
                    PluginLoadOutcome.BlockedByScan => "Blocked — unsafe code",
                    PluginLoadOutcome.Tampered => "Blocked — file changed on disk",
                    PluginLoadOutcome.Revoked => "Blocked — revoked",
                    PluginLoadOutcome.IncompatibleSdk => "Incompatible SDK",
                    PluginLoadOutcome.ManifestInvalid => "Bad manifest",
                    _ => "Failed to load",
                };
                isProblem = problem.Outcome is not PluginLoadOutcome.Disabled;
            }

            Rows.Add(new PluginRow(key, key, problem.Reason, label,
                isProblem,
                CanEnable: (disabledNow || quarantinedNow) && !pendingUninstall,
                CanDisable: false,
                CanUninstall: !pendingUninstall));
        }
    }

    /// <summary>Recompute the catalog from the current verified index + what's installed, then re-apply
    /// the search filter. Cheap and synchronous — safe to call after any install/uninstall.</summary>
    private void RebuildCatalog()
    {
        _allCatalog = PluginCatalog.Build(_feed.Current, _context.PluginsRoot);
        HasUpdates = _allCatalog.Any(i => i.CanUpdate);
        ApplySearch();
    }

    private void ApplySearch()
    {
        CatalogItems.Clear();
        foreach (var item in PluginCatalog.Search(_allCatalog, CatalogSearch))
            CatalogItems.Add(item);
    }

    private IPluginSignatureInspector Inspector() => OperatingSystem.IsWindows()
        ? new AuthenticodeSignatureInspector()
        : new NullSignatureInspector();

    private bool IsPendingUninstall(string key) =>
        _state?.PendingUninstalls.Contains(key, StringComparer.OrdinalIgnoreCase) == true;
}
