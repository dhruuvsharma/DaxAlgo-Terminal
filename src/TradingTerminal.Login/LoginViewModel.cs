using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
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
    private readonly ILogger<LoginViewModel> _logger;

    public LoginViewModel(
        IBrokerSelector brokerSelector,
        IBrokerLoginFormFactory forms,
        SessionContext session,
        ILogger<LoginViewModel> logger)
    {
        _brokerSelector = brokerSelector;
        _session = session;
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
    }

    public IReadOnlyList<IBrokerLoginForm> AvailableForms { get; }

    /// <summary>Typed accessors so XAML can bind each form's UserControl directly without a DataTemplate VM lookup.</summary>
    public IbLoginFormViewModel? IbForm => AvailableForms.OfType<IbLoginFormViewModel>().FirstOrDefault();
    public NinjaLoginFormViewModel? NinjaForm => AvailableForms.OfType<NinjaLoginFormViewModel>().FirstOrDefault();
    public CTraderLoginFormViewModel? CTraderForm => AvailableForms.OfType<CTraderLoginFormViewModel>().FirstOrDefault();
    public AlpacaLoginFormViewModel? AlpacaForm => AvailableForms.OfType<AlpacaLoginFormViewModel>().FirstOrDefault();

    public bool HasIb => IbForm is not null;
    public bool HasNinja => NinjaForm is not null;
    public bool HasCTrader => CTraderForm is not null;
    public bool HasAlpaca => AlpacaForm is not null;

    [ObservableProperty]
    private int _connectedCount;

    [ObservableProperty]
    private string _connectedSummary = "No brokers connected";

    /// <summary>Disabled until at least one broker is in <see cref="ConnectionState.Connected"/>.</summary>
    public bool CanLaunch => ConnectedCount > 0;

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
