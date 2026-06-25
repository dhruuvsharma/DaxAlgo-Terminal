using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.UI;

namespace TradingTerminal.App.Research;

/// <summary>
/// Backs the Settings → Research tab — the Paper Lab reproduction sidecar. Reads current values from
/// <see cref="IOptionsMonitor{T}"/>, edits a local copy, and on Save persists to the per-user JSON file
/// (<see cref="ResearchUserFile"/>), which is layered into configuration with reloadOnChange. Because
/// the ingest/resolver clients read the monitor live, enabling here flips Paper Lab availability without
/// an app restart (reopen the Paper Lab window to pick it up).
/// </summary>
public sealed partial class ResearchSettingsViewModel : ViewModelBase
{
    private readonly IOptionsMonitor<ResearchReproOptions> _options;
    private readonly IOptionsMonitor<SidecarOptions> _sidecar;
    private readonly ILogger<ResearchSettingsViewModel> _logger;

    public ResearchSettingsViewModel(
        IOptionsMonitor<ResearchReproOptions> options,
        IOptionsMonitor<SidecarOptions> sidecar,
        ILogger<ResearchSettingsViewModel> logger)
    {
        _options = options;
        _sidecar = sidecar;
        _logger = logger;
        LoadFromOptions(options.CurrentValue);
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _sidecarBaseUrl = "http://127.0.0.1:8765";
    [ObservableProperty] private int _sidecarTimeoutSeconds = 60;
    [ObservableProperty] private int _retentionDays = 90;

    /// <summary>When on, the app launches the Python sidecar itself on startup (no manual command).</summary>
    [ObservableProperty] private bool _autoLaunchSidecar = true;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasUnsavedChanges;

    partial void OnEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnSidecarBaseUrlChanged(string value) => HasUnsavedChanges = true;
    partial void OnSidecarTimeoutSecondsChanged(int value) => HasUnsavedChanges = true;
    partial void OnRetentionDaysChanged(int value) => HasUnsavedChanges = true;
    partial void OnAutoLaunchSidecarChanged(bool value) => HasUnsavedChanges = true;

    private void LoadFromOptions(ResearchReproOptions o)
    {
        Enabled = o.Enabled;
        SidecarBaseUrl = string.IsNullOrWhiteSpace(o.SidecarBaseUrl) ? "http://127.0.0.1:8765" : o.SidecarBaseUrl;
        SidecarTimeoutSeconds = o.SidecarTimeoutSeconds;
        RetentionDays = o.RetentionDays;
        AutoLaunchSidecar = _sidecar.CurrentValue.AutoStart;
        HasUnsavedChanges = false;
    }

    /// <summary>Loopback port parsed from the sidecar URL (so the managed launcher binds the same port).</summary>
    private int SidecarPort =>
        Uri.TryCreate(SidecarBaseUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.Port : 8765;

    [RelayCommand]
    private void Save()
    {
        var url = SidecarBaseUrl?.Trim() ?? "";

        // The sidecar binds loopback only — refuse anything that isn't 127.0.0.1/localhost so we never
        // persist a config the Http client would reject at call time.
        if (Enabled && !string.IsNullOrEmpty(url) && !IsLoopback(url))
        {
            StatusMessage = "Sidecar URL must be a loopback address (http://127.0.0.1:8765 / localhost).";
            return;
        }

        var next = new ResearchReproOptions
        {
            Enabled = Enabled,
            SidecarBaseUrl = url,
            SidecarTimeoutSeconds = Math.Max(1, SidecarTimeoutSeconds),
            RetentionDays = Math.Max(0, RetentionDays),
            SandboxKind = _options.CurrentValue.SandboxKind,
            JobDatabasePath = _options.CurrentValue.JobDatabasePath,
        };

        try
        {
            ResearchUserFile.Save(next, AutoLaunchSidecar, SidecarPort);
            HasUnsavedChanges = false;
            StatusMessage = Enabled
                ? $"Saved to {ResearchUserFile.Path}. Reopen Paper Lab to use the sidecar."
                : $"Saved to {ResearchUserFile.Path}.";
            _logger.LogInformation("Research repro settings saved (enabled={Enabled})", Enabled);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            _logger.LogError(ex, "Failed to save research repro settings");
        }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
