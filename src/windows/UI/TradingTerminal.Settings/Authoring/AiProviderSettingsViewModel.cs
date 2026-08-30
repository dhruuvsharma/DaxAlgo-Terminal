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
        _apiVersion = config?.ApiVersion ?? string.Empty;
        Kind = config?.Kind ?? AiCodegenProviderKind.OpenAiCompatible;
        IsUserDefined = config?.IsUserDefined ?? false;
    }

    /// <summary>Which wire shape this provider speaks — what decides whether the endpoint is called
    /// the OpenAI way, the Anthropic way, or the Azure way.</summary>
    public AiCodegenProviderKind Kind { get; }

    /// <summary>True for a provider the user added, which is therefore theirs to remove. A shipped
    /// one is layered under <c>appsettings.json</c> and would come back on the next start, so
    /// offering to delete it would be offering something that does not work.</summary>
    public bool IsUserDefined { get; }

    /// <summary>Azure's <c>api-version</c>. Shown only for an Azure row, where it is required.</summary>
    [ObservableProperty] private string _apiVersion;

    /// <summary>True when this row needs the api-version field.</summary>
    public bool IsAzure => Kind == AiCodegenProviderKind.AzureOpenAi;

    /// <summary>On Azure the "model" is the deployment name, which is often not the model id — saying
    /// so in the label is cheaper than the 404 that follows from guessing.</summary>
    public string ModelLabel => IsAzure ? "Deployment" : "Model";

    /// <summary>Model ids this provider actually serves, filled by Refresh models. Empty until asked,
    /// because listing them costs a network call the pane should not make on open.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

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

    // -- Adding a provider the app never heard of -----------------------------------------------

    /// <summary>Endpoints that can be added in a click. See <see cref="AiProviderCatalog"/> for why it
    /// carries no model ids.</summary>
    public IReadOnlyList<AiProviderPreset> Presets => AiProviderCatalog.Presets;

    [ObservableProperty] private AiProviderPreset? _selectedPreset = AiProviderCatalog.Presets[0];

    /// <summary>The name for a provider being added.</summary>
    [ObservableProperty] private string _newProviderName = string.Empty;

    /// <summary>The endpoint for a provider being added.</summary>
    [ObservableProperty] private string _newProviderBaseUrl = string.Empty;

    /// <summary>True when the chosen preset needs a name and a URL from the user.</summary>
    public bool IsCustomPreset => SelectedPreset?.IsBlank ?? false;

    partial void OnSelectedPresetChanged(AiProviderPreset? value)
    {
        OnPropertyChanged(nameof(IsCustomPreset));
        if (value is { IsBlank: false }) NewProviderBaseUrl = value.BaseUrl;
    }

    /// <summary>
    /// Adds the chosen provider and selects it.
    ///
    /// <para>Applied to the live options object as well as written to the user file, for the reason
    /// <see cref="SaveProviderCommand"/> gives: the factory reads that object on every build, so
    /// persisting alone would leave the new provider invisible until the next restart.</para>
    /// </summary>
    [RelayCommand]
    private void AddProvider()
    {
        if (SelectedPreset is not { } preset)
        {
            Status = "Pick a provider to add.";
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewProviderName) ? preset.DisplayName : NewProviderName.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(NewProviderBaseUrl) ? preset.BaseUrl : NewProviderBaseUrl.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Status = "That provider needs a base URL.";
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Caught here rather than at the first generation, which would report it as the provider
            // being unavailable and send the user looking at their key.
            Status = "The base URL must be a full http:// or https:// address.";
            return;
        }

        var id = AiProviderCatalog.UniqueId(
            preset.IsBlank ? AiProviderCatalog.IdFrom(name) : preset.Id,
            _options.Providers.Keys);

        var config = preset.ToProvider();
        config.BaseUrl = baseUrl;
        config.DisplayName = name;

        _options.Providers[id] = config;

        try
        {
            AiCodegenUserFile.SaveProvider(id, config);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not persist the new provider {Provider}", id);
            Status = $"Added for this session, but could not be saved: {ex.Message}";
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        NewProviderName = string.Empty;
        NewProviderBaseUrl = preset.IsBlank ? string.Empty : preset.BaseUrl;

        Refresh();
        Selected = Providers.FirstOrDefault(p => p.ProviderId == id) ?? Selected;
        Status = preset.IsLocal
            ? $"Added {name}. It needs no key - press Refresh models once the server is running."
            : $"Added {name}. Paste a key, then press Refresh models.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a provider the user added, and forgets its key.</summary>
    [RelayCommand]
    private void RemoveProvider(AiProviderSetupRow? row)
    {
        if (row is null) return;

        if (!row.IsUserDefined)
        {
            // Layered under appsettings.json, so it would be back on the next start. Saying so beats a
            // delete button that appears to do nothing.
            Status = $"{row.DisplayName} ships with the app, so it cannot be removed. Clear its key instead.";
            return;
        }

        _options.Providers.Remove(row.ProviderId);

        try
        {
            AiCodegenUserFile.RemoveProvider(row.ProviderId);
            _keys?.Remove(row.ProviderId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not remove the provider {Provider}", row.ProviderId);
        }

        if (_options.DefaultProvider.Equals(row.ProviderId, StringComparison.OrdinalIgnoreCase))
            _options.DefaultProvider = string.Empty;

        Status = $"Removed {row.DisplayName}.";
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks the endpoint what models this key can reach, and says what it answered.
    ///
    /// <para>This is the only control here that proves anything. Everything else reports what was
    /// configured; a model list coming back proves the URL resolves, the key is accepted, and the wire
    /// kind is the one this endpoint speaks - the three ways adding a provider actually goes wrong,
    /// told apart before a whole generation is spent finding out.</para>
    /// </summary>
    [RelayCommand]
    private async Task RefreshModelsAsync(AiProviderSetupRow? row)
    {
        if (row is null) return;

        var client = _builder.WithSettings(row.ProviderId, row.Model, CodegenEffort.Default);
        if (client is null)
        {
            Status = $"{row.DisplayName} is not configured yet.";
            return;
        }

        Status = $"Asking {row.DisplayName} what it serves...";

        // Never throws by contract - a failed lookup is an empty list - so the two outcomes are "some
        // models" and "none", and neither needs a catch here.
        var models = await client.ListModelsAsync().ConfigureAwait(true);

        row.AvailableModels.Clear();
        foreach (var model in models) row.AvailableModels.Add(model);

        if (models.Count == 0)
        {
            Status = row.HasKey || row.IsCli
                ? $"{row.DisplayName} returned no models. Check the base URL and the wire kind, and that the key belongs to this endpoint."
                : $"{row.DisplayName} returned no models - it has no key yet.";
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Model)) row.Model = models[0];
        Status = $"{row.DisplayName} serves {models.Count} model(s).";
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
        if (row.IsAzure) config.ApiVersion = row.ApiVersion?.Trim() ?? string.Empty;

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
