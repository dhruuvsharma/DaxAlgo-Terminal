using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    public AiProviderSetupRow(IStrategyCodegenClient client, bool hasKey, AiCodegenProvider? config)
    {
        ArgumentNullException.ThrowIfNull(client);

        ProviderId = client.ProviderId;
        DisplayName = client.DisplayName;
        _isAvailable = client.IsAvailable;
        _hasKey = hasKey;
        _model = config?.Model ?? string.Empty;
        _baseUrl = config?.BaseUrl ?? string.Empty;
        _cliProfile = config?.CliProfile ?? string.Empty;
        _apiVersion = config?.ApiVersion ?? string.Empty;
        Kind = config?.Kind ?? AiCodegenProviderKind.OpenAiCompatible;
        IsUserDefined = config?.IsUserDefined ?? false;

        var brand = AiProviderBranding.For(ProviderId, DisplayName);
        Mark = brand.Mark;
        Accent = brand.Accent;
        Blurb = brand.Blurb;

        // The shortlist the app already knows, so the model box is a dropdown from the first moment
        // rather than an empty field — the provider's own list replaces it once it has been asked.
        foreach (var model in client.KnownModels) AvailableModels.Add(model);
    }

    /// <summary>
    /// The dropdown's pick, written straight through to <see cref="Model"/>.
    ///
    /// <para>Separate from <see cref="Model"/> because the box is editable: its text commits when focus
    /// leaves, which is right for typing and wrong for a click on the list — a pick has to count the
    /// moment it is made. A blank pick is the box losing its match while somebody types, not a choice,
    /// so it is ignored.</para>
    /// </summary>
    public string? PickedModel
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) Model = value;
        }
    }

    /// <summary>True while the provider is being asked what it serves.</summary>
    [ObservableProperty] private bool _isLoadingModels;

    /// <summary>Whether Anthropic's CLI is on this machine — what decides between "press Sign in" and
    /// "install this first". Only meaningful when <see cref="SupportsSignIn"/>.</summary>
    public bool CliInstalled { get; init; }

    /// <summary>What the last sign-in check found: true, false, or null when it has not run yet.</summary>
    public bool? SignInState { get; init; }

    /// <summary>Who is signed in — <c>you@example.com (Org)</c> — when that is known.</summary>
    public string? SignedInAccount { get; init; }

    /// <summary>The badge monogram — see <see cref="AiProviderBranding"/> for why it is not a logo.</summary>
    public string Mark { get; }

    /// <summary>The badge colour, <c>#RRGGBB</c>.</summary>
    public string Accent { get; }

    /// <summary>One line saying what this provider is and what it needs.</summary>
    public string Blurb { get; }

    /// <summary>
    /// True when this provider can be reached by signing in as well as by a key.
    ///
    /// <para><b>One provider, one row.</b> Anthropic used to appear TWICE — once wanting a key and once
    /// called "signed in" — so the pane asked for an API key on the row whose entire point was not
    /// needing one. They are two credentials for one provider, and that is a choice INSIDE the row.</para>
    /// </summary>
    public bool SupportsSignIn { get; init; }

    /// <summary>Whether the browser sign-in is usable — the CLI installed and somebody signed in.</summary>
    public bool SignInAvailable { get; init; }

    /// <summary>Which credential this provider uses. Only meaningful when <see cref="SupportsSignIn"/>.</summary>
    [ObservableProperty] private bool _useSignIn;

    /// <summary>True when the key box belongs on screen: every row that is not currently signing in.</summary>
    public bool TakesKey => !(SupportsSignIn && UseSignIn);

    /// <summary>True when the sign-in controls belong on screen.</summary>
    public bool IsSignIn => SupportsSignIn && UseSignIn;

    /// <summary>
    /// The segment's own value: checked means API key.
    ///
    /// <para>The inverse of <see cref="UseSignIn"/> as a real two-way property rather than a converter
    /// in the view. A converter would put the meaning of the switch in the XAML, where it cannot be
    /// tested and has to be read backwards.</para>
    /// </summary>
    public bool UseApiKey
    {
        get => !UseSignIn;
        set => UseSignIn = !value;
    }

    /// <summary>True when "Use this provider" has something to do: it works, and it is not already the
    /// one in use.</summary>
    public bool CanBecomeDefault => IsReady && !IsDefault;

    partial void OnUseSignInChanged(bool value)
    {
        OnPropertyChanged(nameof(UseApiKey));
        OnPropertyChanged(nameof(TakesKey));
        OnPropertyChanged(nameof(IsSignIn));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Signal));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanBecomeDefault));
        OnPropertyChanged(nameof(Group));
    }

    /// <summary>
    /// Which of the three groups this row sits in.
    ///
    /// <para>Grouping is what makes the list answerable without clicking: "which of these works?" is
    /// twelve identical cards' worth of reading when they are all one list, and one glance when the
    /// working ones are under a heading that says so.</para>
    /// </summary>
    public string Group => IsDefault ? "IN USE" : IsReady ? "READY" : "NEEDS SETUP";

    /// <summary>
    /// Three words for the state of this row, for the pill at its right edge.
    ///
    /// <para>The pane used to say this only in a sentence inside the detail pane of whichever row was
    /// selected, so the answer to "which of these actually works?" required clicking every one.</para>
    /// </summary>
    public string Signal => IsReady
        ? "Ready"
        : IsSignIn ? "Not signed in" : "Needs a key";

    /// <summary>Usable RIGHT NOW, by the credential this row is set to use.</summary>
    public bool IsReady => IsSignIn ? SignInAvailable : IsAvailable;

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
    public string StatusText => IsSignIn
        ? SignInAvailable
            ? SignInState == true
                ? (SignedInAccount is { } who ? $"Signed in as {who}." : "Signed in.")
                  + " Billed per token to that organisation — API access, not a Claude subscription."
                : "Anthropic's CLI is installed — checking whether you are signed in…"
            : CliInstalled
                ? "Not signed in. Press Sign in: your browser opens, you approve, and you are done."
                : "Signing in needs Anthropic's CLI (ant), which is not installed yet. Get it below, "
                  + "or switch to API key."
        : HasKey
            ? "Key saved, encrypted for this Windows account only."
            : "No key yet. Paste one to enable this provider.";

    partial void OnIsAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Signal));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(CanBecomeDefault));
    }

    partial void OnIsDefaultChanged(bool value)
    {
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(CanBecomeDefault));
    }

    partial void OnHasKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Signal));
        OnPropertyChanged(nameof(CanBecomeDefault));
    }
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
        ILogger<AiProviderSettingsViewModel>? logger = null,
        AnthropicOAuthCli? oauth = null)
    {
        // Injectable, and it matters twice. The pane's Sign in button and the FACTORY's sign-in client
        // both ask "is the CLI installed?", and two independently constructed wrappers can disagree —
        // the pane said no while the provider list said yes. It is also the only way a test can drive
        // the button without the CLI being installed on whoever is running the suite.
        _oauth = oauth ?? new AnthropicOAuthCli();

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

    /// <summary>The browser sign-in, shared with the provider factory so the two cannot disagree.</summary>
    private readonly AnthropicOAuthCli _oauth;

    /// <summary>Whether the sign-in button can do anything: the `ant` CLI has to be installed.</summary>
    public bool CanSignIn => _oauth.IsInstalled;

    /// <summary>True once a check has confirmed somebody is signed in — what swaps the Sign in button
    /// for the account and a Sign out.</summary>
    public bool IsSignedIn => _oauth.IsInstalled && _oauth.SignedIn == true;

    /// <summary>True when the Sign in button belongs on screen: the CLI is there and nobody is known
    /// to be signed in.</summary>
    public bool ShowSignInButton => _oauth.IsInstalled && !IsSignedIn;

    /// <summary>Who is signed in, for the line above Sign out.</summary>
    public string SignedInAccount => _oauth.Account is { } account
        ? $"Signed in as {account}"
        : "Signed in";

    /// <summary>True while the pane is asking the CLI who is signed in.</summary>
    [ObservableProperty] private bool _isCheckingSignIn;

    /// <summary>What the sign-in half says about itself.</summary>
    public string SignInHint => !_oauth.IsInstalled
        ? "Signing in is handled by Anthropic's own CLI, which is not installed. Download it, run the "
          + "installer or put ant.exe on your PATH, then press Recheck — or paste an API key instead."
        : IsSignedIn
            ? "Billed per token to this organisation — API access, not a Claude Pro or Max subscription."
            : "Your browser opens; approve access and come back here. Billed per token to the "
              + "organisation you pick — API access, not a Claude Pro or Max subscription.";

    /// <summary>
    /// Asks the CLI who is signed in, then rebuilds the rows from the answer.
    ///
    /// <para>Run when the pane opens. Before it, "installed" was reported as "signed in", so a machine
    /// with the CLI and no sign-in showed a Ready provider whose every request failed.</para>
    /// </summary>
    [RelayCommand]
    private async Task CheckSignInAsync()
    {
        if (!_oauth.IsInstalled || IsCheckingSignIn) return;

        IsCheckingSignIn = true;
        try
        {
            await _oauth.IsSignedInAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // IsSignedInAsync already swallows a missing or hung CLI; anything else is still "we could
            // not tell", which must not take the pane down.
            _logger?.LogWarning(ex, "Checking the Anthropic sign-in failed.");
        }
        finally
        {
            IsCheckingSignIn = false;
        }

        Refresh();
        RaiseSignInChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the CLI's download page — the way to the Sign in button for anyone without Go.</summary>
    [RelayCommand]
    private void OpenCliDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AnthropicOAuthCli.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not open {Url}", AnthropicOAuthCli.DownloadUrl);
            Status = $"Could not open the browser. The CLI is at {AnthropicOAuthCli.DownloadUrl}";
        }
    }

    private void RaiseSignInChanged()
    {
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(ShowSignInButton));
        OnPropertyChanged(nameof(SignedInAccount));
        OnPropertyChanged(nameof(SignInHint));
    }

    /// <summary>
    /// The documented way to get the CLI, shown beside the disabled button so the dead end is a next
    /// step instead of a shrug.
    ///
    /// <para>Deliberately not a one-click installer: this downloads and installs another vendor's
    /// program, which is the user's decision to make knowingly, not something a settings pane should do
    /// on their behalf while they are looking at a strategy builder.</para>
    /// </summary>
    public string SignInInstallHint =>
        "go install github.com/anthropics/anthropic-cli/cmd/ant@latest"
        + "  —  or a release from github.com/anthropics/anthropic-cli/releases";

    [ObservableProperty] private bool _isSigningIn;

    /// <summary>
    /// Opens the browser sign-in and rebuilds the picker from what it left behind.
    ///
    /// <para>The rebuild matters for the same reason it does after a key is saved: provider clients
    /// capture their credential when they are constructed, so a picker left alone would go on showing
    /// "not set up" for a provider that now works.</para>
    /// </summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;

        IsSigningIn = true;
        try
        {
            Status = "Waiting for the browser sign-in…";
            var result = await _oauth.SignInAsync().ConfigureAwait(true);
            Status = result.Message;

            if (result.Success)
            {
                Refresh();
                Changed?.Invoke(this, EventArgs.Empty);
            }

            RaiseSignInChanged();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Anthropic sign-in failed.");
            Status = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    /// <summary>Signs out and rebuilds, so the picker stops offering a provider that no longer works.</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        Status = await _oauth.SignOutAsync().ConfigureAwait(true)
            ? "Signed out of Anthropic."
            : "Could not sign out — the CLI reported a failure.";

        Refresh();
        RaiseSignInChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The keyed Anthropic provider, which the browser sign-in folds into.</summary>
    private const string AnthropicProviderId = "anthropic";

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

        // The sign-in client is FOLDED INTO the provider it signs into rather than listed beside it.
        // Two rows called Anthropic — one wanting a key, one called "signed in" — is what made the pane
        // ask for an API key on the row whose whole point is not needing one.
        // ONCE. IAiStrategyBuilder.Providers is a computed property — the real one is
        // factory.BuildAll() — so every read returns freshly constructed clients. Enumerating it twice
        // and matching by reference compares two different objects and silently matches nothing, which
        // is how the merge below quietly became a no-op and Anthropic appeared twice again.
        var clients = _builder.Providers.ToList();

        var signIn = clients.FirstOrDefault(c =>
            c.ProviderId.Equals(StrategyCodegenClientFactory.AnthropicOAuthId, StringComparison.OrdinalIgnoreCase));

        var signedInIsDefault = StrategyCodegenClientFactory.AnthropicOAuthId
            .Equals(_options.DefaultProvider, StringComparison.OrdinalIgnoreCase);

        foreach (var client in clients)
        {
            if (client.ProviderId.Equals(
                    StrategyCodegenClientFactory.AnthropicOAuthId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var carriesSignIn = signIn is not null
                && client.ProviderId.Equals(AnthropicProviderId, StringComparison.OrdinalIgnoreCase);

            _options.Providers.TryGetValue(client.ProviderId, out var config);
            var hasKey = _keys?.HasKey(client.ProviderId) ?? false;

            Add(new AiProviderSetupRow(client, hasKey, config)
            {
                SupportsSignIn = carriesSignIn,
                SignInAvailable = carriesSignIn && signIn!.IsAvailable,
                CliInstalled = carriesSignIn && _oauth.IsInstalled,
                SignInState = carriesSignIn ? _oauth.SignedIn : null,
                SignedInAccount = carriesSignIn ? _oauth.Account : null,

                // Sign in only when signing in is actually POSSIBLE.
                //
                // This read `signedInIsDefault || !hasKey`, so a machine with no key opened the row on
                // the sign-in half — where the button is disabled unless the `ant` CLI is installed.
                // The user got a dead button and no key box, on the one row they needed to configure:
                // "I'm unable to click on sign in with Anthropic, it's blocked". Defaulting into a half
                // that cannot be used is worse than not offering it.
                //
                // INSTALLED, not signed in: a signed-out CLI still has a working Sign in button, and
                // that button is the easiest way there is to set this row up.
                UseSignIn = carriesSignIn
                    && (signedInIsDefault || (!hasKey && _oauth.IsInstalled)),

                IsDefault = client.ProviderId.Equals(_options.DefaultProvider, StringComparison.OrdinalIgnoreCase)
                    || (carriesSignIn && signedInIsDefault),
            });
        }

        // Nothing was folded in, so offer it on its own rather than losing it — an edition without the
        // Anthropic provider would otherwise have no way to sign in at all.
        if (signIn is not null && !Providers.Any(r => r.SupportsSignIn))
        {
            _options.Providers.TryGetValue(signIn.ProviderId, out var signInConfig);
            Add(new AiProviderSetupRow(signIn, hasKey: false, signInConfig)
            {
                SupportsSignIn = true,
                SignInAvailable = signIn.IsAvailable,
                CliInstalled = _oauth.IsInstalled,
                SignInState = _oauth.SignedIn,
                SignedInAccount = _oauth.Account,
                UseSignIn = true,
                IsDefault = signedInIsDefault,
            });
        }

        Selected = Providers.FirstOrDefault(p => p.ProviderId == selectedId)
            ?? Providers.FirstOrDefault(p => p.IsReady)
            ?? Providers.FirstOrDefault();
    }

    /// <summary>Lists a row, with the models the provider already said it serves and a watch on its
    /// model so a pick is saved without a Save button.</summary>
    private void Add(AiProviderSetupRow row)
    {
        if (_liveModels.TryGetValue(ListingId(row), out var live) && live.Count > 0)
        {
            row.AvailableModels.Clear();
            foreach (var model in live) row.AvailableModels.Add(model);
        }

        row.PropertyChanged += OnRowPropertyChanged;
        Providers.Add(row);
    }

    // -- The model --------------------------------------------------------------------------------

    /// <summary>
    /// What each provider answered when asked what it serves, by the id it was asked under.
    ///
    /// <para>Kept across <see cref="Refresh"/>, which rebuilds every row after any save — without it,
    /// saving a key would throw away the list the user had just been shown and ask the network again.</para>
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _liveModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The provider id to ask for a row's models: the SIGN-IN client when the row is on its sign-in half.
    ///
    /// <para>Asking under the row's own id built the keyed client, which on the sign-in half has no key
    /// — so Refresh answered "returned no models" to a user who was signed in and could reach all of
    /// them.</para>
    /// </summary>
    private static string ListingId(AiProviderSetupRow row) =>
        row.IsSignIn && !row.ProviderId.Equals(StrategyCodegenClientFactory.AnthropicOAuthId, StringComparison.OrdinalIgnoreCase)
            ? StrategyCodegenClientFactory.AnthropicOAuthId
            : row.ProviderId;

    /// <summary>A model picked or typed is the model — saved the moment it is committed.</summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiProviderSetupRow.Model) && sender is AiProviderSetupRow row)
            SaveModel(row);
    }

    /// <summary>
    /// Writes a row's model to the live options and the user file.
    ///
    /// <para>No <see cref="Refresh"/>: rebuilding every row here would pull the list out from under the
    /// dropdown the user is still looking at. The model is read at build time, so the options object
    /// is all that has to change for the next generation to use it.</para>
    /// </summary>
    private void SaveModel(AiProviderSetupRow row)
    {
        var model = row.Model?.Trim() ?? string.Empty;

        if (!_options.Providers.TryGetValue(row.ProviderId, out var config))
        {
            config = new AiCodegenProvider { Kind = row.Kind };
            _options.Providers[row.ProviderId] = config;
        }

        if (string.Equals(config.Model, model, StringComparison.Ordinal)) return;
        config.Model = model;

        try
        {
            AiCodegenUserFile.SaveProvider(row.ProviderId, config);
            Status = model.Length == 0
                ? $"{row.DisplayName} will use its own default model."
                : $"{row.DisplayName} will build with {model}.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not persist the model for {Provider}", row.ProviderId);
            Status = $"Using {model} for this session, but it could not be saved: {ex.Message}";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fills a row's dropdown from the provider the first time it is opened, so the list is what this
    /// key can actually call rather than one compiled in advance.
    ///
    /// <para>On OPENING THE DROPDOWN, not on opening the pane or selecting the row: listing costs a
    /// network call, and the moment somebody asks to see the list is the one moment it is wanted. The
    /// shortlist is on screen meanwhile, so nothing waits on the answer.</para>
    /// </summary>
    [RelayCommand]
    private Task LoadModelsOnDemandAsync(AiProviderSetupRow? row) =>
        row is { IsReady: true } && !_liveModels.ContainsKey(ListingId(row))
            ? LoadModelsAsync(row, quiet: true)
            : Task.CompletedTask;

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
    private Task RefreshModelsAsync(AiProviderSetupRow? row) =>
        row is null ? Task.CompletedTask : LoadModelsAsync(row, quiet: false);

    /// <summary>
    /// Asks the row's provider what it serves and puts the answer in the row's dropdown.
    /// </summary>
    /// <param name="quiet">True for the automatic load when a row's dropdown is opened: it says nothing
    /// unless it finds something, and an empty answer leaves the shortlist in place rather than an empty
    /// box.</param>
    private async Task LoadModelsAsync(AiProviderSetupRow row, bool quiet)
    {
        if (row.IsLoadingModels) return;

        var client = _builder.WithSettings(ListingId(row), row.Model, CodegenEffort.Default);
        if (client is null)
        {
            if (!quiet) Status = $"{row.DisplayName} is not configured yet.";
            return;
        }

        if (quiet)
        {
            row.IsLoadingModels = true;
            try
            {
                var found = await client.ListModelsAsync().ConfigureAwait(true);
                if (found.Count == 0) return;

                _liveModels[ListingId(row)] = found;
                row.AvailableModels.Clear();
                foreach (var model in found) row.AvailableModels.Add(model);
                if (string.IsNullOrWhiteSpace(row.Model)) row.Model = found[0];
            }
            catch (Exception ex)
            {
                // Never thrown by contract; a fake or a future client that does must not surface as an
                // unobserved task exception from a row the user only clicked on.
                _logger?.LogDebug(ex, "Listing models for {Provider} failed", row.ProviderId);
            }
            finally
            {
                row.IsLoadingModels = false;
            }

            return;
        }

        // Said before the request, because after it every failure looks the same. A base URL that cannot
        // become an absolute http(s) address never reaches the network, so reporting "returned no
        // models" would blame the provider for a typo -- and "localhost:1234/v1" with the scheme left
        // off is the commonest one there is.
        // The more specific diagnosis first. An unedited template is syntactically a fine URL, so the
        // check below would pass it and the user would get a DNS failure naming a host they never chose.
        if (row.TakesKey && CodegenBaseUrl.IsUnedited(row.BaseUrl))
        {
            Status = $"{row.DisplayName}: replace {CodegenBaseUrl.Placeholder} in the base URL with your "
                + "own resource name first.";
            return;
        }

        if (row.TakesKey && CodegenBaseUrl.TryAbsolute(CodegenBaseUrl.Normalise(row.BaseUrl)) is null)
        {
            Status = string.IsNullOrWhiteSpace(row.BaseUrl)
                ? $"{row.DisplayName} has no base URL yet."
                : $"\"{row.BaseUrl}\" is not a usable base URL - it needs a host, over http or https.";
            return;
        }

        Status = $"Asking {row.DisplayName} what it serves...";

        // Never throws by contract - a failed lookup is an empty list - so the two outcomes are "some
        // models" and "none", and neither needs a catch here.
        IReadOnlyList<string> models;
        row.IsLoadingModels = true;
        try
        {
            models = await client.ListModelsAsync().ConfigureAwait(true);
        }
        finally
        {
            row.IsLoadingModels = false;
        }

        row.AvailableModels.Clear();
        foreach (var model in models) row.AvailableModels.Add(model);

        if (models.Count == 0)
        {
            _liveModels.Remove(ListingId(row));
            Status = row.HasKey || row.IsSignIn
                ? $"{row.DisplayName} returned no models. Check the base URL and the wire kind, and that the key belongs to this endpoint."
                : $"{row.DisplayName} returned no models - it has no key yet.";
            return;
        }

        _liveModels[ListingId(row)] = models;
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
        if (row.TakesKey) config.BaseUrl = row.BaseUrl?.Trim() ?? string.Empty;
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

        if (!row.IsReady)
        {
            Status = row.IsSignIn
                ? $"{row.DisplayName} is not signed in yet, so it cannot be the default."
                : $"{row.DisplayName} is not set up yet, so it cannot be the default.";
            return;
        }

        // One row, two credentials, two provider ids underneath. Which one gets written is the segment's
        // answer — the factory builds a different client for each, and they bill different accounts.
        var chosenId = row.IsSignIn ? StrategyCodegenClientFactory.AnthropicOAuthId : row.ProviderId;

        _options.DefaultProvider = chosenId;
        try
        {
            AiCodegenUserFile.SaveDefaultProvider(chosenId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not persist the default provider");
        }

        foreach (var other in Providers) other.IsDefault = ReferenceEquals(other, row);
        Status = $"{row.DisplayName} is now the default.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-probes PATH and the key store. What the user presses after installing a CLI in another
    /// window, so being told it worked does not require restarting the terminal.</summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        // The sign-in too: "I signed in from a terminal, now what?" is answered by this button.
        if (_oauth.IsInstalled) await _oauth.IsSignedInAsync().ConfigureAwait(true);
        RaiseSignInChanged();

        Refresh();
        var ready = Providers.Count(p => p.IsReady);
        Status = ready == 0
            ? "Still nothing set up — add an API key, or install an agent CLI and sign in with it."
            : $"{ready} provider(s) ready.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
