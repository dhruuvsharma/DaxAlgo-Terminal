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

    // ── Presentation metadata (drives the single login DataTemplate) ──────────────────────────────
    // Badge / colour / subtitle / category used to be hand-written 12× in LoginWindow.xaml. They now
    // live here, keyed by broker, so the markup is one template and adding a broker is one table row.

    private BrokerTile Tile => Tiles.TryGetValue(Broker, out var t) ? t : FallbackTile;

    /// <summary>Two/three-letter square-badge text (e.g. "BN", "IB").</summary>
    public string Badge => Tile.Badge;
    /// <summary>Badge background as a hex string (bound through StringToBrushConverter).</summary>
    public string BadgeColor => Tile.BadgeColor;
    /// <summary>Badge foreground hex — dark on the light/yellow badges, white elsewhere.</summary>
    public string BadgeForeground => Tile.BadgeForeground;
    /// <summary>One-line "transport · assets · auth" descriptor shown under the broker name.</summary>
    public string Subtitle => Tile.Subtitle;

    public LoginCategory Category => Tile.Category;

    /// <summary>Group-header label the login list groups rows under.</summary>
    public string CategoryName => Category switch
    {
        LoginCategory.Keyless => "Keyless · instant, no API key",
        LoginCategory.Credentialed => "Credentialed",
        _ => "Local bridge",
    };

    /// <summary>Sort key so groups render Keyless → Credentialed → Local bridge.</summary>
    public int CategoryOrder => (int)Category;

    /// <summary>Zero-credential public-data brokers — highlighted as the first-run path.</summary>
    public bool IsKeyless => Category == LoginCategory.Keyless;

    private bool _isExpanded;
    /// <summary>Accordion state — the <see cref="LoginViewModel"/> enforces one-open-at-a-time.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private sealed record BrokerTile(
        string Badge, string BadgeColor, string BadgeForeground, string Subtitle, LoginCategory Category);

    private static readonly BrokerTile FallbackTile = new("?", "#555555", "#FFFFFF", string.Empty, LoginCategory.Credentialed);

    private static readonly IReadOnlyDictionary<BrokerKind, BrokerTile> Tiles = new Dictionary<BrokerKind, BrokerTile>
    {
        [BrokerKind.InteractiveBrokers] = new("IB", "#D32F2F", "#FFFFFF", "TWS API · localhost socket", LoginCategory.LocalBridge),
        [BrokerKind.NinjaTrader]        = new("NT", "#1B5E20", "#FFFFFF", "NTDirect · futures, AT Interface", LoginCategory.LocalBridge),
        [BrokerKind.CTrader]            = new("CT", "#0277BD", "#FFFFFF", "Spotware OAuth · FX + CFD, L2 depth", LoginCategory.Credentialed),
        [BrokerKind.Alpaca]             = new("AL", "#FFB300", "#1E2026", "REST + WebSocket · stocks + crypto", LoginCategory.Credentialed),
        [BrokerKind.IronBeam]           = new("IRB", "#D84315", "#FFFFFF", "REST + WebSocket · futures, L1/L2 + tape", LoginCategory.Credentialed),
        [BrokerKind.LondonStrategicEdge] = new("LSE", "#1565C0", "#FFFFFF", "WS + REST · free multi-asset L1 + history · API key", LoginCategory.Credentialed),
        [BrokerKind.Upstox]             = new("UP", "#5C2D91", "#FFFFFF", "REST + WebSocket · NSE/BSE depth · OAuth2", LoginCategory.Credentialed),
        [BrokerKind.Binance]            = new("BN", "#F0B90B", "#1E2026", "Public WebSocket · live crypto, L2 depth", LoginCategory.Keyless),
        [BrokerKind.Coinbase]           = new("CB", "#0052FF", "#FFFFFF", "Public WebSocket · live crypto, L2 depth", LoginCategory.Keyless),
        [BrokerKind.Bybit]              = new("BY", "#F7A600", "#17181E", "Public WebSocket · live crypto, L2 depth", LoginCategory.Keyless),
        [BrokerKind.Kraken]             = new("KR", "#5741D9", "#FFFFFF", "Public WebSocket · live crypto, L2 depth", LoginCategory.Keyless),
        [BrokerKind.Okx]                = new("OK", "#121212", "#FFFFFF", "Public WebSocket · live crypto, L2 depth", LoginCategory.Keyless),
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

/// <summary>How a broker is grouped on the login screen. Ordering doubles as the section order:
/// the zero-credential keyless exchanges come first (the recommended first-run path), then the
/// credentialed brokers, then the brokers that bridge to a local app/socket.</summary>
public enum LoginCategory
{
    Keyless = 0,
    Credentialed = 1,
    LocalBridge = 2,
}
