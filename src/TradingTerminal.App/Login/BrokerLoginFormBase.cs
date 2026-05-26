using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login;

/// <summary>
/// Shared connect-state machine for the per-broker login form view-models. Each per-broker
/// VM (Ib/Ninja/cTrader/Alpaca) inherits this and supplies its own credential fields +
/// <see cref="IBrokerLoginForm.ApplyToOptions"/>; the base wires the Connect / Disconnect
/// commands, subscribes to the broker's per-broker <see cref="IBrokerSelector.StateOf"/>
/// stream, and exposes <see cref="CurrentState"/> / <see cref="IsConnecting"/> /
/// <see cref="ErrorMessage"/> for status-pill binding.
///
/// NOTE: deliberately NOT partial / no <c>[ObservableProperty]</c> — the WPF compiler's
/// MarkupCompilePass1 runs in a temporary _wpftmp.csproj that doesn't always cooperate with
/// source generators on partial classes used in <c>DataTemplate DataType="{x:Type ...}"</c>.
/// Manual <c>SetProperty</c> avoids that flake.
/// </summary>
public abstract class BrokerLoginFormBase : ViewModelBase, IBrokerLoginForm, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    protected readonly IBrokerSelector Selector;
    protected readonly ILogger Logger;
    private IDisposable? _stateSub;

    protected BrokerLoginFormBase(IBrokerSelector selector, ILogger logger)
    {
        Selector = selector;
        Logger = logger;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect);
    }

    public abstract BrokerKind Broker { get; }
    public abstract string DisplayName { get; }
    public abstract bool CanSubmit { get; }
    public abstract void ApplyToOptions();
    public abstract string GetSessionAccountLabel();
    public abstract string GetTimeoutErrorMessage();
    public abstract string GetFailureMessage();
    public abstract void Load();
    public abstract void Save();

    private ConnectionState _currentState = ConnectionState.Disconnected;
    public ConnectionState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(StatusText));
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsConnected => CurrentState == ConnectionState.Connected;
    public bool IsDisconnected => CurrentState is ConnectionState.Disconnected or ConnectionState.Failed;

    private bool _isConnecting;
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string StatusText => CurrentState switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Reconnecting => "Reconnecting…",
        ConnectionState.Failed => "Failed",
        _ => "Not connected",
    };

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }

    /// <summary>Called once after the LoginViewModel finishes constructing all forms so this form
    /// can hydrate from persisted creds and subscribe to its broker's state stream. Idempotent.</summary>
    public void Initialize()
    {
        if (_stateSub is not null) return;
        Load();
        if (Selector.IsAvailable(Broker))
        {
            // Broker state events arrive on whatever thread the underlying client emitted on
            // (OpenClient task continuation for cTrader, EReader callback for IB, etc.). Marshal
            // to the UI dispatcher before mutating CurrentState — WPF's auto-marshaling for
            // PropertyChanged is unreliable enough on the expander pill that the status text
            // was staying stuck on "Not connected" after a successful connect.
            _stateSub = Selector.StateOf(Broker).Subscribe(s =>
            {
                if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                    d.BeginInvoke(new Action(() => CurrentState = s));
                else
                    CurrentState = s;
            });
        }
    }

    private bool CanConnect() => !IsConnecting && CurrentState != ConnectionState.Connected && CanSubmit;
    private bool CanDisconnect() => !IsConnecting && CurrentState == ConnectionState.Connected;

    private async Task ConnectAsync()
    {
        if (!CanSubmit)
        {
            ErrorMessage = "Fill in the required fields before connecting.";
            return;
        }
        ErrorMessage = null;
        IsConnecting = true;
        try
        {
            ApplyToOptions();

            using var cts = new CancellationTokenSource(ConnectTimeout);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = Selector.StateOf(Broker).Subscribe(state =>
            {
                if (state == ConnectionState.Connected) tcs.TrySetResult(true);
                else if (state == ConnectionState.Failed) tcs.TrySetResult(false);
            });
            using var ctReg = cts.Token.Register(() => tcs.TrySetCanceled());

            await Selector.ConnectAsync(Broker, cts.Token).ConfigureAwait(true);
            var ok = await tcs.Task.ConfigureAwait(true);

            if (!ok)
            {
                ErrorMessage = GetFailureMessage();
                return;
            }

            Save();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = GetTimeoutErrorMessage();
            Logger.LogWarning("Login connection timed out (broker={Broker})", Broker);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Logger.LogError(ex, "Login connection failed for {Broker}", Broker);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task DisconnectAsync()
    {
        ErrorMessage = null;
        IsConnecting = true;
        try
        {
            await Selector.DisconnectAsync(Broker).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Logger.LogWarning(ex, "Disconnect failed for {Broker}", Broker);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public void Dispose()
    {
        _stateSub?.Dispose();
        _stateSub = null;
    }
}
