using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

/// <summary>
/// Orchestrator for the login window. Holds the active per-broker
/// <see cref="IBrokerLoginForm"/>, mediates broker-tile selection, runs the connect flow,
/// and exposes status/error/connection state for the XAML to bind to. All broker-specific
/// fields and copy live in the per-broker form view-models.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _brokerSelector;
    private readonly IBrokerLoginFormFactory _forms;
    private readonly CredentialStore _credentialStore;
    private readonly SessionContext _session;
    private readonly ILogger<LoginViewModel> _logger;
    private readonly IDisposable _stateSub;

    public LoginViewModel(
        IMarketDataRepository repository,
        IBrokerSelector brokerSelector,
        IBrokerLoginFormFactory forms,
        CredentialStore credentialStore,
        SessionContext session,
        ILogger<LoginViewModel> logger)
    {
        _repository = repository;
        _brokerSelector = brokerSelector;
        _forms = forms;
        _credentialStore = credentialStore;
        _session = session;
        _logger = logger;

        AvailableForms = _forms.All;
        if (AvailableForms.Count == 0)
            throw new InvalidOperationException(
                "No broker forms available — build with at least one broker SDK present (TWS API, NTDirect.dll, or always-on cTrader).");

        // Restore the last-used broker if it's available; otherwise fall back to the first one.
        var stored = _credentialStore.Load();
        var preferred = AvailableForms.FirstOrDefault(f => f.Broker == stored.SelectedBroker)
                     ?? AvailableForms[0];
        ActiveForm = preferred;

        _stateSub = _repository.ConnectionState.Subscribe(s => CurrentState = s);
    }

    public IReadOnlyList<IBrokerLoginForm> AvailableForms { get; }

    /// <summary>Typed accessors so XAML can bind each form's UserControl directly without a DataTemplate VM lookup.</summary>
    public IbLoginFormViewModel? IbForm => AvailableForms.OfType<IbLoginFormViewModel>().FirstOrDefault();
    public NinjaLoginFormViewModel? NinjaForm => AvailableForms.OfType<NinjaLoginFormViewModel>().FirstOrDefault();
    public CTraderLoginFormViewModel? CTraderForm => AvailableForms.OfType<CTraderLoginFormViewModel>().FirstOrDefault();
    public AlpacaLoginFormViewModel? AlpacaForm => AvailableForms.OfType<AlpacaLoginFormViewModel>().FirstOrDefault();

    public bool IsIbBrokerSelected => ActiveForm.Broker == BrokerKind.InteractiveBrokers;
    public bool IsNinjaBrokerSelected => ActiveForm.Broker == BrokerKind.NinjaTrader;
    public bool IsCTraderBrokerSelected => ActiveForm.Broker == BrokerKind.CTrader;
    public bool IsAlpacaBrokerSelected => ActiveForm.Broker == BrokerKind.Alpaca;

    [ObservableProperty]
    private IBrokerLoginForm _activeForm = null!;

    partial void OnActiveFormChanged(IBrokerLoginForm value)
    {
        value.Load();
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(ModeDisplayName));
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(IsLiveMode));
        OnPropertyChanged(nameof(IsIbBrokerSelected));
        OnPropertyChanged(nameof(IsNinjaBrokerSelected));
        OnPropertyChanged(nameof(IsCTraderBrokerSelected));
        OnPropertyChanged(nameof(IsAlpacaBrokerSelected));
    }

    public string SubtitleText => $"Sign in via {ActiveForm.DisplayName}";
    public string ModeDisplayName => ActiveForm is null ? string.Empty : ResolveMode().DisplayName;
    public string ModeDescription => ActiveForm is null ? string.Empty : ResolveMode().Description;
    public bool IsLiveMode => ActiveForm is not null && ResolveMode().IsLive;

    private BrokerConnectionMode ResolveMode()
    {
        // The selector's ActiveMode reflects whichever broker was most recently flipped.
        // For the mode badge during form selection (before the user clicks "Sign in"),
        // momentarily switch the selector so the badge mirrors the highlighted form.
        if (_brokerSelector.ActiveKind != ActiveForm.Broker)
            _brokerSelector.SetActive(ActiveForm.Broker);
        return _brokerSelector.ActiveMode;
    }

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private ConnectionState _currentState = ConnectionState.Disconnected;

    /// <summary>Raised with <c>true</c> when a connection succeeded; <c>false</c> if the user cancelled.</summary>
    public event EventHandler<bool>? LoginCompleted;

    [RelayCommand]
    private void SelectForm(IBrokerLoginForm? form)
    {
        if (form is null) return;
        ActiveForm = form;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        StatusMessage = $"Connecting to {ActiveForm.DisplayName}...";
        IsConnecting = true;
        IsConnected = false;

        try
        {
            ActiveForm.ApplyToOptions();
            _brokerSelector.SetActive(ActiveForm.Broker);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = _repository.ConnectionState.Subscribe(state =>
            {
                if (state == ConnectionState.Connected) tcs.TrySetResult(true);
                else if (state == ConnectionState.Failed) tcs.TrySetResult(false);
            });
            using var ctReg = cts.Token.Register(() => tcs.TrySetCanceled());

            await _repository.ConnectAsync(cts.Token);
            var ok = await tcs.Task.ConfigureAwait(true);

            if (!ok)
            {
                ErrorMessage = ActiveForm.GetFailureMessage();
                StatusMessage = null;
                return;
            }

            ActiveForm.Save();
            _session.SetSignedIn(string.Empty, ActiveForm.GetSessionAccountLabel());

            IsConnected = true;
            StatusMessage = "Connected · loading workspace...";
            await Task.Delay(700).ConfigureAwait(true);

            LoginCompleted?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = ActiveForm.GetTimeoutErrorMessage();
            StatusMessage = null;
            _logger.LogWarning("Login connection timed out (broker={Broker})", ActiveForm.Broker);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = null;
            _logger.LogError(ex, "Login connection failed");
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => LoginCompleted?.Invoke(this, false);

    public void Dispose() => _stateSub.Dispose();
}
