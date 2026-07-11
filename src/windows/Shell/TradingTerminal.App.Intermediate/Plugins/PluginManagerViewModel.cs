using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TradingTerminal.Infrastructure.Plugins;
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
/// Plugin Manager view-model (View → "Manage strategy plugins…"). Shows every plugin the loader
/// SAW — loaded ones and, crucially, every one that did not load with its classified reason
/// (failed / quarantined / trust-rejected / SDK-incompatible / disabled) — and drives the lifecycle:
/// install, enable/disable, re-enable after quarantine, uninstall. Lifecycle changes are persisted
/// in <see cref="PluginStateStore"/> and take effect on the next start (a loaded plugin's assembly
/// is file-locked by its rooted load context), so mutations raise the restart banner.
/// </summary>
public sealed partial class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginHostContext _context;
    private readonly PluginStateStore? _state;

    public PluginManagerViewModel(PluginHostContext context)
    {
        _context = context;
        _state = context.State;
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
    }

    public string PluginsRoot { get; }
    public string TrustPolicySummary { get; }
    public ObservableCollection<PluginRow> Rows { get; } = new();

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True once any lifecycle change (enable/disable/uninstall/install) is pending a
    /// restart — drives the banner. Loaded plugins are file-locked, so nothing applies live.</summary>
    [ObservableProperty] private bool _restartRequired;

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

        IPluginSignatureInspector inspector = OperatingSystem.IsWindows()
            ? new AuthenticodeSignatureInspector()
            : new NullSignatureInspector();

        // Packages are sha256-verified and carry private deps; a raw .dll is the dev drop-in path.
        // Both go through the same manifest/SDK/trust gates.
        var result = dialog.FileName.EndsWith(DaxPluginPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? PluginInstaller.InstallFromPackage(
                dialog.FileName, _context.PluginsRoot, _context.TrustPolicy, inspector, _state)
            : PluginInstaller.InstallFromDll(
                dialog.FileName, _context.PluginsRoot, _context.TrustPolicy, inspector, _state);
        Status = result.Message;
        if (result.Success) RestartRequired = true;
        Rebuild();
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
    }

    /// <summary>Open the plugins folder in the file explorer so a developer can drop a package in by hand.</summary>
    [RelayCommand]
    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(_context.PluginsRoot);
        Process.Start(new ProcessStartInfo { FileName = _context.PluginsRoot, UseShellExecute = true });
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
            var label = pendingUninstall ? "Loaded — uninstalls on restart"
                : disabled ? "Loaded — disables on restart"
                : "Loaded";
            Rows.Add(new PluginRow(key, plugin.Name, $"SDK {plugin.TargetSdkVersion}", label,
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

    private bool IsPendingUninstall(string key) =>
        _state?.PendingUninstalls.Contains(key, StringComparer.OrdinalIgnoreCase) == true;
}
