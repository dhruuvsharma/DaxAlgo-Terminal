using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

/// <summary>
/// Orchestrator for the multi-broker login window. Hosts every registered
/// <see cref="IBrokerLoginForm"/> as its own expander — each form drives its own
/// <see cref="IBrokerSelector"/> connect lifecycle independently. The shell only owns the
/// bottom <c>Launch</c> button, which becomes enabled once at least one broker is
/// <see cref="ConnectionState.Connected"/> and dismisses the window when clicked.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly IBrokerSelector _brokerSelector;
    private readonly SessionContext _session;
    private readonly IQuestDbLauncher _questDb;
    private readonly CredentialStore _credentialStore;
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(
        IBrokerSelector brokerSelector,
        IBrokerLoginFormFactory forms,
        SessionContext session,
        IQuestDbLauncher questDb,
        CredentialStore credentialStore,
        ILogger<LoginViewModel> logger)
    {
        _brokerSelector = brokerSelector;
        _session = session;
        _questDb = questDb;
        _credentialStore = credentialStore;
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

        // Aggregate state changes from the selector so the Launch button enable-state updates
        // whenever any broker connects or disconnects.
        _brokerSelector.StateChanged += OnSelectorStateChanged;

        RefreshConnectedSummary();
        InitializeQuestDb();

        // Hydrate the persisted Auto Connect preference straight into the backing field so the
        // OnAutoConnectChanged persistence hook doesn't fire during construction.
        _autoConnect = _credentialStore.Load().AutoConnect;
        if (_autoConnect) AutoConnectAll();
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
        foreach (var form in AvailableForms.OfType<BrokerLoginFormBase>())
        {
            if (!form.ConnectCommand.CanExecute(null)) continue;
            started++;
            _ = form.ConnectCommand.ExecuteAsync(null);
        }
        _logger.LogInformation("Auto Connect: started {Count} broker connection attempt(s)", started);
    }

    /// <summary>QuestDB is the only market-data backend that needs an external server up before the
    /// terminal can persist ticks. We surface its status on the login screen and, when auto-start is on,
    /// kick the Docker launch off in the background here — so it warms up (and re-arms the store) while
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

    public IReadOnlyList<IBrokerLoginForm> AvailableForms { get; }

    /// <summary>Typed accessors so XAML can bind each form's UserControl directly without a DataTemplate VM lookup.</summary>
    public IbLoginFormViewModel? IbForm => AvailableForms.OfType<IbLoginFormViewModel>().FirstOrDefault();
    public NinjaLoginFormViewModel? NinjaForm => AvailableForms.OfType<NinjaLoginFormViewModel>().FirstOrDefault();
    public CTraderLoginFormViewModel? CTraderForm => AvailableForms.OfType<CTraderLoginFormViewModel>().FirstOrDefault();
    public AlpacaLoginFormViewModel? AlpacaForm => AvailableForms.OfType<AlpacaLoginFormViewModel>().FirstOrDefault();
    public BinanceLoginFormViewModel? BinanceForm => AvailableForms.OfType<BinanceLoginFormViewModel>().FirstOrDefault();
    public IronBeamLoginFormViewModel? IronBeamForm => AvailableForms.OfType<IronBeamLoginFormViewModel>().FirstOrDefault();

    public bool HasIb => IbForm is not null;
    public bool HasNinja => NinjaForm is not null;
    public bool HasCTrader => CTraderForm is not null;
    public bool HasAlpaca => AlpacaForm is not null;
    public bool HasBinance => BinanceForm is not null;
    public bool HasIronBeam => IronBeamForm is not null;

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

    /// <summary>A start attempt is in flight (Docker Desktop / container coming up).</summary>
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

    private void OnFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The form's IsConnected property bounces on every CurrentState change; piggyback there
        // to keep CanLaunch in sync without polling.
        if (e.PropertyName is nameof(BrokerLoginFormBase.IsConnected))
            RefreshConnectedSummary();
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
        _ => kind.ToString(),
    };

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
