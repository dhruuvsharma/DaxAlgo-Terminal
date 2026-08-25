using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// One provider's setup row: what it is, whether it can be used yet, and what is missing when it cannot.
///
/// <para>Two shapes behind one row. A <b>keyed</b> provider needs an API key, an endpoint and a model, and
/// the user pays that vendor per token. An <b>agent CLI</b> needs none of those — it is a program on PATH
/// that owns its own login, so the only question is whether it is installed.</para>
/// </summary>
public sealed partial class AiProviderSetupRow : ObservableObject
{
    public AiProviderSetupRow(IStrategyCodegenClient client, bool isCli, bool hasKey, AiCodegenProvider? config)
    {
        ArgumentNullException.ThrowIfNull(client);

        ProviderId = client.ProviderId;
        DisplayName = client.DisplayName;
        IsCli = isCli;
        _isAvailable = client.IsAvailable;
        _hasKey = hasKey;
        _model = config?.Model ?? string.Empty;
        _baseUrl = config?.BaseUrl ?? string.Empty;
        _cliProfile = config?.CliProfile ?? string.Empty;
    }

    public string ProviderId { get; }

    public string DisplayName { get; }

    /// <summary>True for an installed-program provider (Claude Code, Codex, Gemini CLI). Keyless: the
    /// vendor's tool owns the login, so this pane must not ask for a key it has no use for.</summary>
    public bool IsCli { get; }

    /// <summary>Keyed providers are the ones with a key, an endpoint and a model to edit.</summary>
    public bool IsKeyed => !IsCli;

    [ObservableProperty] private bool _isAvailable;

    [ObservableProperty] private bool _hasKey;

    [ObservableProperty] private string _model;

    [ObservableProperty] private string _baseUrl;

    [ObservableProperty] private string _cliProfile;

    /// <summary>The key being typed. Cleared on save — a pasted secret has no reason to outlive the
    /// click, and this object lives as long as the window does.</summary>
    [ObservableProperty] private string _keyEntry = string.Empty;

    [ObservableProperty] private bool _isDefault;

    /// <summary>What the row says about itself, in the user's terms rather than the config's.</summary>
    public string StatusText => IsCli
        ? IsAvailable
            ? "Installed and ready — it signs in with its own account."
            : "Not found. Install it, sign in with the vendor's own tool, then press Recheck."
        : HasKey
            ? "Key saved, encrypted for this Windows account only."
            : "No key yet. Paste one to enable this provider.";

    partial void OnIsAvailableChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnHasKeyChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

/// <summary>
/// The provider setup pane: paste an API key, point a provider at a different endpoint or model, or check
/// whether an agent CLI is installed.
///
/// <para>Built because the composer's provider footer reported that nothing was configured and offered no
/// way to configure anything — a dead end that told the user to go and find a settings page. This is that
/// page, opened from the footer that raises the problem.</para>
///
/// <para><b>Keys never enter this class's persisted state.</b> They go from the box to
/// <see cref="IAiKeyStore"/>, which encrypts them under the current Windows account, and the entry is
/// cleared on the way out. Everything else — endpoint, model, CLI profile, which provider is the default —
/// is non-secret and goes to the user config file.</para>
/// </summary>
public sealed partial class AiProviderSettingsViewModel : ObservableObject
{
    private readonly IAiStrategyBuilder _builder;
    private readonly IAiKeyStore? _keys;
    private readonly AiCodegenOptions _options;
    private readonly ILogger<AiProviderSettingsViewModel>? _logger;

    public AiProviderSettingsViewModel(
        IAiStrategyBuilder builder,
        IOptions<AiCodegenOptions>? options = null,
        IAiKeyStore? keys = null,
        ILogger<AiProviderSettingsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _builder = builder;
        _options = options?.Value ?? new AiCodegenOptions();
        // Optional: an edition without a key store can still install and select an agent CLI, which is a
        // complete way to use the builder. It just cannot hold a key, and the pane says so.
        _keys = keys;
        _logger = logger;

        Providers = [];
        Refresh();
    }

    /// <summary>Every provider the app knows how to build, configured or not — the unconfigured ones are
    /// the whole point of the pane.</summary>
    public ObservableCollection<AiProviderSetupRow> Providers { get; }

    [ObservableProperty] private AiProviderSetupRow? _selected;

    [ObservableProperty] private string _status = string.Empty;

    /// <summary>False when no key store is wired — the key fields then say why, rather than failing on
    /// Save.</summary>
    public bool CanStoreKeys => _keys is not null;

    /// <summary>Raised after any change to which providers are usable, so the composer that opened this
    /// window can rebuild its picker without polling.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Rebuilds every row from the live provider list.
    ///
    /// <para>Re-reading rather than editing in place is what makes this correct: a keyed client captures
    /// its key at construction, so one built before the key was saved goes on reporting itself unavailable
    /// no matter what the row says.</para>
    /// </summary>
    public void Refresh()
    {
        var selectedId = Selected?.ProviderId;
        Providers.Clear();

        foreach (var client in _builder.Providers)
        {
            var isCli = AgentCliAdapter.All.Any(a =>
                a.ProviderId.Equals(client.ProviderId, StringComparison.OrdinalIgnoreCase));

            _options.Providers.TryGetValue(client.ProviderId, out var config);

            Providers.Add(new AiProviderSetupRow(
                client,
                isCli,
                hasKey: _keys?.HasKey(client.ProviderId) ?? false,
                config)
            {
                IsDefault = client.ProviderId.Equals(_options.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            });
        }

        Selected = Providers.FirstOrDefault(p => p.ProviderId == selectedId)
            ?? Providers.FirstOrDefault(p => p.IsAvailable)
            ?? Providers.FirstOrDefault();
    }

    /// <summary>Stores the pasted key and rebuilds, so the row reports what is true rather than what was
    /// intended.</summary>
    [RelayCommand]
    private void SaveKey(AiProviderSetupRow? row)
    {
        if (row is null) return;

        if (_keys is null)
        {
            Status = "This edition has no key store, so keys cannot be saved. Install an agent CLI instead.";
            return;
        }

        var key = row.KeyEntry?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            Status = "Paste a key first.";
            return;
        }

        try
        {
            _keys.Set(row.ProviderId, key);
        }
        catch (Exception ex)
        {
            // The store encrypts under the Windows account, which can fail on a roaming or damaged
            // profile. Saying so beats a row that silently stays unavailable.
            _logger?.LogError(ex, "Could not store the API key for {Provider}", row.ProviderId);
            Status = $"Could not save the key: {ex.Message}";
            return;
        }
        finally
        {
            // Out of memory whatever happened above.
            row.KeyEntry = string.Empty;
        }

        Status = $"{row.DisplayName} is ready.";
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the stored key.</summary>
    [RelayCommand]
    private void ClearKey(AiProviderSetupRow? row)
    {
        if (row is null || _keys is null) return;

        _keys.Remove(row.ProviderId);
        row.KeyEntry = string.Empty;
        Status = $"Removed the stored key for {row.DisplayName}.";
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Saves the endpoint, model and CLI profile for a row.
    ///
    /// <para>Written to the user file <b>and</b> applied to the options object the client factory holds,
    /// because the factory reads that object on every build. Persisting alone would leave the change
    /// invisible until the next restart, which reads as a control that does nothing.</para>
    /// </summary>
    [RelayCommand]
    private void SaveProvider(AiProviderSetupRow? row)
    {
        if (row is null) return;

        if (!_options.Providers.TryGetValue(row.ProviderId, out var config))
        {
            config = new AiCodegenProvider();
            _options.Providers[row.ProviderId] = config;
        }

        config.Model = row.Model?.Trim() ?? string.Empty;
        config.CliProfile = row.CliProfile?.Trim() ?? string.Empty;
        if (row.IsKeyed) config.BaseUrl = row.BaseUrl?.Trim() ?? string.Empty;

        try
        {
            AiCodegenUserFile.SaveProvider(row.ProviderId, config);
            Status = $"Saved {row.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not persist provider settings for {Provider}", row.ProviderId);
            Status = $"Applied for this session, but could not be saved: {ex.Message}";
        }

        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes this the provider the builder opens with.</summary>
    [RelayCommand]
    private void MakeDefault(AiProviderSetupRow? row)
    {
        if (row is null) return;

        if (!row.IsAvailable)
        {
            Status = $"{row.DisplayName} is not set up yet, so it cannot be the default.";
            return;
        }

        _options.DefaultProvider = row.ProviderId;
        try
        {
            AiCodegenUserFile.SaveDefaultProvider(row.ProviderId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not persist the default provider");
        }

        foreach (var other in Providers) other.IsDefault = other.ProviderId == row.ProviderId;
        Status = $"{row.DisplayName} is now the default.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-probes PATH and the key store. What the user presses after installing a CLI in another
    /// window, so being told it worked does not require restarting the terminal.</summary>
    [RelayCommand]
    private void Recheck()
    {
        Refresh();
        var ready = Providers.Count(p => p.IsAvailable);
        Status = ready == 0
            ? "Still nothing set up — add an API key, or install an agent CLI and sign in with it."
            : $"{ready} provider(s) ready.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
