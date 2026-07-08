using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.UI;

namespace TradingTerminal.App.Plugins;

/// <summary>One row in the installed-plugins list.</summary>
public sealed record LoadedPluginRow(string Name, string TargetSdkVersion, string AssemblyPath);

/// <summary>
/// Plugin Manager view-model (View → "Manage strategy plugins…"). Lists the strategy plugins loaded
/// at startup, shows where the plugins folder is and which trust policy is in force, and installs a
/// new plugin package — validating it (manifest + signature, no code execution) and copying it into
/// the plugins folder. New plugins activate on the next app start.
/// </summary>
public sealed partial class PluginManagerViewModel : ViewModelBase
{
    private readonly PluginHostContext _context;

    public PluginManagerViewModel(PluginHostContext context)
    {
        _context = context;
        PluginsRoot = context.PluginsRoot;
        TrustPolicySummary = context.TrustPolicy.RequireSignature
            ? "Curated mode — only plugins signed by a trusted publisher will load."
            : "Permissive (developer mode) — unsigned local plugins load.";

        foreach (var plugin in context.LoadedPlugins)
            LoadedPlugins.Add(new LoadedPluginRow(plugin.Name, plugin.TargetSdkVersion, plugin.AssemblyPath));

        Status = LoadedPlugins.Count == 0
            ? "No plugins loaded yet. Install one below, then restart the app."
            : $"{LoadedPlugins.Count} plugin(s) loaded.";
    }

    public string PluginsRoot { get; }
    public string TrustPolicySummary { get; }
    public ObservableCollection<LoadedPluginRow> LoadedPlugins { get; } = new();

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Pick a plugin's main .dll, validate it against the active trust policy + SDK version,
    /// and copy the package into the plugins folder. Activation is on next startup.</summary>
    [RelayCommand]
    private void InstallPlugin()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the plugin's main .dll",
            Filter = "Plugin assembly (*.dll)|*.dll",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        IPluginSignatureInspector inspector = OperatingSystem.IsWindows()
            ? new AuthenticodeSignatureInspector()
            : new NullSignatureInspector();

        var result = PluginInstaller.InstallFromDll(dialog.FileName, _context.PluginsRoot, _context.TrustPolicy, inspector);
        Status = result.Message;
    }

    /// <summary>Open the plugins folder in the file explorer so a developer can drop a package in by hand.</summary>
    [RelayCommand]
    private void OpenPluginsFolder()
    {
        Directory.CreateDirectory(_context.PluginsRoot);
        Process.Start(new ProcessStartInfo { FileName = _context.PluginsRoot, UseShellExecute = true });
    }
}
