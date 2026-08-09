using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Core.Session;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

/// <summary>
/// Orchestrator for the multi-broker login window. Hosts every registered
/// <see cref="IBrokerLoginForm"/> as a row in one grouped, filterable accordion list
/// (<see cref="FormsView"/>) — each form drives its own <see cref="IBrokerSelector"/> connect
/// lifecycle independently. The shell only owns the search box, the per-group "Connect all"
/// action, and the bottom <c>Launch</c> button, which becomes enabled once at least one broker is
/// <see cref="ConnectionState.Connected"/> and dismisses the window when clicked.
///
/// <para>Rows are projected through a single <c>DataTemplate</c> (see <c>LoginWindow.xaml</c> +
/// <see cref="BrokerFormHost"/>): grouping comes from <see cref="BrokerLoginFormBase.CategoryName"/>,
/// the accordion is enforced here (one expanded at a time), and the last-connected broker is
/// pre-expanded on open.</para>
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly IBrokerSelector _brokerSelector;
    private readonly SessionContext _session;
    private readonly IQuestDbLauncher _questDb;
    private readonly CredentialStore _credentialStore;
    private readonly ITelegramArchiveLogin _telegramLogin;
    private readonly ILogger<LoginViewModel> _logger;

    /// <summary>The forms as their concrete base type, pre-sorted Keyless → Credentialed → Local,
    /// then by name. Backing list for <see cref="FormsView"/> and the accordion/group commands.</summary>
    private readonly List<BrokerLoginFormBase> _formItems;

    /// <summary>Guards the accordion collapse-others pass from re-entering via PropertyChanged.</summary>
    private bool _collapsingOthers;

    public LoginViewModel(
        IBrokerSelector brokerSelector,
        IBrokerLoginFormFactory forms,
        SessionContext session,
        IQuestDbLauncher questDb,
        CredentialStore credentialStore,
        ITelegramArchiveLogin telegramLogin,
        ILogger<LoginViewModel> logger)
    {
        _brokerSelector = brokerSelector;
        _session = session;
        _questDb = questDb;
        _credentialStore = credentialStore;
        _telegramLogin = telegramLogin;
        _logger = logger;

        AvailableForms = forms.All;
        if (AvailableForms.Count == 0)
            throw new InvalidOperationException(
                "No broker forms available — build with at least one broker SDK present (TWS API, NTDirect.dll, cTrader, Alpaca).");

        // Hydrate each form's persisted credentials and subscribe each to its broker's state stream.
        foreach (var form in AvailableForms.OfType<BrokerLoginFormBase>())
        {
            form.Initialize();
            form.PropertyChanged += OnFormPropertyChanged;
        }

        _formItems = AvailableForms.OfType<BrokerLoginFormBase>()
            .OrderBy(f => f.CategoryOrder)
            .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Flat filterable view. Source order (above) gives Keyless → Credentialed → Local bridge,
        // then alphabetical within each tier. No GroupDescriptions — the redesigned UI is a flat list.
        var view = new ListCollectionView(_formItems) { Filter = FilterForm };
        FormsView = view;

        // Aggregate state changes from the selector so the Launch button enable-state updates
        // whenever any broker connects or disconnects.
        _brokerSelector.StateChanged += OnSelectorStateChanged;

        RefreshConnectedSummary();
        InitializeQuestDb();
        InitializeTelegramArchive();
        BuildServices();

        var stored = _credentialStore.Load();
        PreExpandLastBroker(stored.SelectedBroker);

        // Hydrate the persisted Auto Connect preference straight into the backing field so the
        // OnAutoConnectChanged persistence hook doesn't fire during construction.
        _autoConnect = stored.AutoConnect;
        if (_autoConnect) AutoConnectAll();
    }

    public IReadOnlyList<IBrokerLoginForm> AvailableForms { get; }

    /// <summary>Grouped + filtered broker rows the login list binds to. Items are
    /// <see cref="BrokerLoginFormBase"/>; the group key is <see cref="BrokerLoginFormBase.CategoryName"/>.</summary>
    public ICollectionView FormsView { get; }

    /// <summary>Live search term — filters the broker rows by name / badge / category.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => FormsView.Refresh();

    private bool FilterForm(object obj)
    {
        if (obj is not BrokerLoginFormBase f) return false;
        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Badge.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.CategoryName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pre-expands the last-connected broker (or the first keyless one on a fresh install),
    /// so returning users land on the form they actually use.</summary>
    private void PreExpandLastBroker(BrokerKind last)
    {
        var target = _formItems.FirstOrDefault(f => f.Broker == last)
                     ?? _formItems.FirstOrDefault(f => f.IsKeyless)
                     ?? _formItems.FirstOrDefault();
        if (target is not null) target.IsExpanded = true;
    }

    /// <summary>Fires Connect on every ready form in the named category (e.g. "Keyless · instant…").
    /// Bound to each group header's "Connect all" button via the group's <see cref="CollectionViewGroup.Name"/>.</summary>
    [RelayCommand]
    private void ConnectGroup(string? categoryName)
    {
        if (string.IsNullOrEmpty(categoryName)) return;
        var started = 0;
        foreach (var f in _formItems.Where(f => f.CategoryName == categoryName))
        {
            if (!f.ConnectCommand.CanExecute(null)) continue;
            started++;
            _ = f.ConnectCommand.ExecuteAsync(null);
        }
        _logger.LogInformation("Connect group '{Category}': started {Count} attempt(s)", categoryName, started);
    }

    // ── Auto Connect ─────────────────────────────────────────────────────────────────────────────

    /// <summary>When ticked, the terminal fires every available broker's Connect (with its saved
    /// credentials) as soon as the login window opens on the next launch. Persisted immediately.</summary>
    [ObservableProperty]
    private bool _autoConnect;

    partial void OnAutoConnectChanged(bool value)
    {
        // Load-modify-save so we never clobber credentials a form saved in the meantime.
        var stored = _credentialStore.Load();
        stored.AutoConnect = value;
        _credentialStore.Save(stored);
    }

    /// <summary>Fires Connect on every broker form that is ready to submit (saved credentials,
    /// broker SDK present). Each form's own command handles timeout/failure UI independently, so
    /// one unreachable broker never blocks the others.</summary>
    private void AutoConnectAll()
    {
        var started = 0;
        foreach (var form in _formItems)
        {
            if (!form.ConnectCommand.CanExecute(null)) continue;
            started++;
            _ = form.ConnectCommand.ExecuteAsync(null);
        }
        _logger.LogInformation("Auto Connect: started {Count} broker connection attempt(s)", started);
    }

    /// <summary>QuestDB is the only market-data backend that needs an external server up before the
    /// terminal can persist ticks. We surface its status on the login screen and, when auto-start is on,
    /// kick native startup off in the background here — so it warms up (and re-arms the store) while
    /// the user is signing in, rather than stalling the main window later.</summary>
    private void InitializeQuestDb()
    {
        ShowQuestDb = _questDb.IsApplicable;
        if (!ShowQuestDb) return;

        if (_questDb.IsReachable())
        {
            QuestDbReady = true;
            QuestDbStatus = "QuestDB ready";
            return;
        }

        QuestDbStatus = "QuestDB not running";
        if (_questDb.AutoStart)
            _ = StartQuestDbInternalAsync(); // fire-and-forget warm-up; status updates as it progresses
    }

    [ObservableProperty]
    private int _connectedCount;

    [ObservableProperty]
    private string _connectedSummary = "No brokers connected";

    /// <summary>Disabled until at least one broker is in <see cref="ConnectionState.Connected"/>.</summary>
    public bool CanLaunch => ConnectedCount > 0;

    // ── QuestDB warm-up (only shown when QuestDB is the configured tick backend) ──────────────────

    /// <summary>True when QuestDB is the configured backend — gates the status pill + button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQuestDbCommand))]
    private bool _showQuestDb;

    /// <summary>QuestDB is up and the store is persisting.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQuestDbCommand))]
    private bool _questDbReady;

    /// <summary>A QuestDB start attempt is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartQuestDbCommand))]
    private bool _isQuestDbBusy;

    /// <summary>Human-readable QuestDB status shown on the login screen.</summary>
    [ObservableProperty]
    private string _questDbStatus = "QuestDB";

    private bool CanStartQuestDb() => ShowQuestDb && !IsQuestDbBusy && !QuestDbReady;

    /// <summary>Manual retry for the QuestDB launch (also runs automatically when auto-start is on).</summary>
    [RelayCommand(CanExecute = nameof(CanStartQuestDb))]
    private Task StartQuestDb() => StartQuestDbInternalAsync();

    private async Task StartQuestDbInternalAsync()
    {
        if (IsQuestDbBusy) return;
        IsQuestDbBusy = true; // NotifyCanExecuteChangedFor keeps the button in sync
        QuestDbStatus = "Starting QuestDB…";
        try
        {
            var ok = await _questDb.StartAsync().ConfigureAwait(true);
            QuestDbReady = ok;
            QuestDbStatus = ok ? "QuestDB ready" : "QuestDB unavailable — click to retry";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QuestDB launch from the login screen failed");
            QuestDbStatus = "QuestDB error — click to retry";
        }
        finally
        {
            IsQuestDbBusy = false;
        }
    }

    public event EventHandler<bool>? LoginCompleted;

    [RelayCommand]
    private void Launch()
    {
        if (!CanLaunch) return;

        // Pick the first connected broker as the session-label source — it's just for the
        // "Signed in as X" tile in the main shell. Multi-broker users see a generic label.
        var connected = _brokerSelector.Connected;
        var primary = connected.Count > 0 ? connected[0] : (BrokerKind?)null;
        var label = primary is { } b
            ? AvailableForms.FirstOrDefault(f => f.Broker == b)?.GetSessionAccountLabel() ?? b.ToString()
            : "Multi-broker session";

        _session.SetSignedIn(string.Empty, label);
        _logger.LogInformation("Launching with {Count} connected broker(s): {Brokers}",
            connected.Count, string.Join(", ", connected));
        LoginCompleted?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => LoginCompleted?.Invoke(this, false);

    private void OnSelectorStateChanged(object? sender, BrokerStateChangedEventArgs e)
    {
        // The selector raises StateChanged from whatever thread the broker emitted on; the form
        // VM mirrors state via its own subscription. We just need to refresh the aggregate counts.
        // Marshal to UI by posting through a synchronization context-aware property change.
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.BeginInvoke(new Action(RefreshConnectedSummary));
        else
            RefreshConnectedSummary();
    }

    private void OnFormPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrokerLoginFormBase form) return;

        // Accordion: when one row expands, collapse the rest (guarded against re-entry).
        if (e.PropertyName == nameof(BrokerLoginFormBase.IsExpanded) && form.IsExpanded && !_collapsingOthers)
        {
            _collapsingOthers = true;
            foreach (var other in _formItems)
                if (!ReferenceEquals(other, form)) other.IsExpanded = false;
            _collapsingOthers = false;
            return;
        }

        // IsConnected bounces on every CurrentState change; piggyback there to keep CanLaunch in
        // sync and to remember the last broker the user actually connected.
        if (e.PropertyName == nameof(BrokerLoginFormBase.IsConnected))
        {
            RefreshConnectedSummary();
            if (form.IsConnected) RememberLastBroker(form.Broker);
        }
    }

    private void RememberLastBroker(BrokerKind broker)
    {
        var stored = _credentialStore.Load();
        if (stored.SelectedBroker == broker) return;
        stored.SelectedBroker = broker;
        _credentialStore.Save(stored);
    }

    private void RefreshConnectedSummary()
    {
        var connected = _brokerSelector.Connected;
        ConnectedCount = connected.Count;
        ConnectedSummary = connected.Count switch
        {
            0 => "No brokers connected",
            1 => $"1 broker connected: {Label(connected[0])}",
            _ => $"{connected.Count} brokers connected: {string.Join(", ", connected.Select(Label))}",
        };
        OnPropertyChanged(nameof(CanLaunch));
        LaunchCommand.NotifyCanExecuteChanged();
    }

    private static string Label(BrokerKind kind) => kind switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        BrokerKind.Binance => "Binance",
        BrokerKind.IronBeam => "Ironbeam",
        BrokerKind.LondonStrategicEdge => "LSE Data",
        BrokerKind.Upstox => "Upstox",
        _ => kind.ToString(),
    };

    // ── Telegram market-data archive login ───────────────────────────────────────────────────────

    /// <summary>api_id from my.telegram.org/apps.</summary>
    [ObservableProperty] private int _telegramApiId;

    /// <summary>api_hash from my.telegram.org/apps.</summary>
    [ObservableProperty] private string _telegramApiHash = "";

    /// <summary>Phone number in international format (e.g. +91XXXXXXXXXX).</summary>
    [ObservableProperty] private string _telegramPhone = "";

    /// <summary>Human-readable status shown on the Telegram tile.</summary>
    [ObservableProperty] private string _telegramStatus = "Not connected";

    /// <summary>True once a signed-in Telegram session is available.</summary>
    [ObservableProperty] private bool _isTelegramConnected;

    /// <summary>A connect attempt is in flight (drives the spinner + disables the button).</summary>
    [ObservableProperty] private bool _isTelegramBusy;

    /// <summary>Hydrate the Telegram tile from the persisted archive credentials (shared with the
    /// in-app Archive Settings tab via archive.json), so returning users see their saved details.</summary>
    private void InitializeTelegramArchive()
    {
        var creds = _telegramLogin.Load();
        TelegramApiId = creds.ApiId;
        TelegramApiHash = creds.ApiHash;
        TelegramPhone = creds.PhoneNumber;
        IsTelegramConnected = _telegramLogin.IsConnected;
        TelegramStatus = IsTelegramConnected ? "Connected" : "Not connected";
    }

    /// <summary>Persists the entered Telegram credentials and signs in (the verification-code / 2FA
    /// dialog pops automatically). Runs the blocking transport work off the UI thread.</summary>
    [RelayCommand]
    private async Task ConnectTelegramAsync()
    {
        if (IsTelegramBusy) return;
        IsTelegramBusy = true;
        TelegramStatus = "Connecting to Telegram…";
        try
        {
            var creds = new TelegramArchiveCredentials(TelegramApiId, TelegramApiHash, TelegramPhone);
            var result = await _telegramLogin.ConnectAsync(creds).ConfigureAwait(true);
            IsTelegramConnected = _telegramLogin.IsConnected;
            TelegramStatus = result.Message;
            if (!result.Success)
                _logger.LogInformation("Telegram archive login from login screen did not complete: {Reason}", result.Message);
        }
        catch (Exception ex)
        {
            TelegramStatus = $"Login failed: {ex.Message}";
            _logger.LogError(ex, "Telegram archive login from login screen failed");
        }
        finally
        {
            IsTelegramBusy = false;
        }
    }

    // ── Services & external dependencies ─────────────────────────────────────────────────────────

    /// <summary>External processes the terminal talks to — surfaced on the login screen so users know
    /// what to start (and can see live status) before signing in.</summary>
    public ObservableCollection<ServiceDependencyViewModel> Services { get; } = new();

    /// <summary>True while a re-check sweep is running (drives the panel's spinner / button state).</summary>
    [ObservableProperty]
    private bool _isCheckingServices;

    private void BuildServices()
    {
        // Broker desktop apps (TWS / IB Gateway, NinjaTrader 8) are NOT listed here — each broker form
        // declares its own prerequisite via BrokerLoginFormBase.Prerequisite and renders it inside that
        // broker's expander. This list contains app-managed optional services such as QuestDB.

        Services.Add(new ServiceDependencyViewModel(
            name: "QuestDB",
            purpose: "Provides the local time-series database for market data.",
            requirement: "Optional",
            howTo: "Start Docker Desktop first, then start QuestDB here or copy the Docker command.",
            startCommand: ServiceDependencyViewModel.QuestDbDockerRunCommand,
            probe: ServiceDependencyViewModel.QuestDbRunningAsync,
            startAction: ServiceDependencyViewModel.StartQuestDbAsync,
            startActionLabel: "Start QuestDB"));

        _ = RecheckServicesAsync();
    }

    /// <summary>Re-probes every service that supports a live status check (parallel, defensive).</summary>
    [RelayCommand]
    private async Task RecheckServicesAsync()
    {
        if (IsCheckingServices) return;
        IsCheckingServices = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await Task.WhenAll(Services.Where(s => s.CanProbe).Select(s => s.CheckAsync(cts.Token)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Service status sweep failed");
        }
        finally
        {
            IsCheckingServices = false;
        }
    }

    /// <summary>One-click start for a service that supports it (for example, QuestDB), then
    /// re-probes its status.</summary>
    [RelayCommand]
    private async Task StartServiceAsync(ServiceDependencyViewModel? service)
    {
        if (service is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await service.RunStartAsync(cts.Token);
    }

    /// <summary>Copies a service's start command to the clipboard so the user can paste it into a terminal.</summary>
    [RelayCommand]
    private void CopyStartCommand(ServiceDependencyViewModel? service)
    {
        if (service?.StartCommand is not { Length: > 0 } cmd) return;
        try { Clipboard.SetText(cmd); }
        catch { /* clipboard can be transiently locked — ignore */ }
    }

    public void Dispose()
    {
        _brokerSelector.StateChanged -= OnSelectorStateChanged;
        foreach (var form in AvailableForms.OfType<BrokerLoginFormBase>())
        {
            form.PropertyChanged -= OnFormPropertyChanged;
            form.Dispose();
        }
    }
}
