using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// The Settings → "AI providers" section for the AI Strategy Builder. Lists every codegen provider with
/// its availability, lets the user paste and store an API key (DPAPI, per user) or clear it, and shows
/// which installed agent CLIs (Claude Code / Codex) were detected. Nothing here calls a provider — it
/// only manages local setup.
/// </summary>
public sealed partial class AiProvidersSettingsViewModel : ViewModelBase
{
    private readonly IAiKeyStore? _keys;

    public AiProvidersSettingsViewModel(IAiStrategyBuilder? builder = null, IAiKeyStore? keys = null)
    {
        _keys = keys;
        Providers = new ObservableCollection<AiProviderRow>();

        foreach (var client in builder?.Providers ?? [])
            Providers.Add(new AiProviderRow(client, keys));

        Status = Providers.Count == 0
            ? "AI codegen isn't wired in this build."
            : $"{Providers.Count(p => p.IsAvailable)} of {Providers.Count} providers ready.";
    }

    public ObservableCollection<AiProviderRow> Providers { get; }

    [ObservableProperty] private string? _status;

    /// <summary>Store (or clear, when blank) the pasted key for a provider, DPAPI-encrypted.</summary>
    [RelayCommand]
    private void SaveKey(AiProviderRow? row)
    {
        if (row is null || _keys is null) return;
        if (string.IsNullOrWhiteSpace(row.KeyInput))
        {
            _keys.Remove(row.ProviderId);
            row.MarkStored(false);
            Status = $"Cleared the {row.DisplayName} key. Restart to apply.";
        }
        else
        {
            _keys.Set(row.ProviderId, row.KeyInput.Trim());
            row.KeyInput = string.Empty;
            row.MarkStored(true);
            Status = $"Stored the {row.DisplayName} key. Restart to apply.";
        }
    }

    [RelayCommand]
    private void ClearKey(AiProviderRow? row)
    {
        if (row is null || _keys is null) return;
        _keys.Remove(row.ProviderId);
        row.KeyInput = string.Empty;
        row.MarkStored(false);
        Status = $"Cleared the {row.DisplayName} key. Restart to apply.";
    }
}

/// <summary>One provider row in the settings list.</summary>
public sealed partial class AiProviderRow : ObservableObject
{
    private readonly IStrategyCodegenClient _client;

    public AiProviderRow(IStrategyCodegenClient client, IAiKeyStore? keys)
    {
        _client = client;
        _hasStoredKey = keys?.HasKey(client.ProviderId) == true;
        // Agent CLIs and Ollama don't take a key here — CLIs own their login, Ollama is keyless local.
        NeedsKey = client.ProviderId is not ("claude-cli" or "codex-cli" or "ollama");
    }

    public string ProviderId => _client.ProviderId;
    public string DisplayName => _client.DisplayName;
    public bool IsAvailable => _client.IsAvailable;
    public bool NeedsKey { get; }

    /// <summary>The pasted key, bound to a password box (cleared after Save so it isn't kept in memory).</summary>
    [ObservableProperty] private string _keyInput = string.Empty;

    [ObservableProperty] private bool _hasStoredKey;

    public string StatusText => IsAvailable
        ? (HasStoredKey ? "Ready (key stored)" : NeedsKey ? "Ready" : "Ready")
        : NeedsKey ? "Add an API key" : "Not detected — install the CLI";

    public void MarkStored(bool stored)
    {
        HasStoredKey = stored;
        OnPropertyChanged(nameof(StatusText));
    }
}
