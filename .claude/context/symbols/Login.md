# TradingTerminal.Login — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Shell/TradingTerminal.Login/AiKeyStore.cs
```cs
   18: public sealed class AiKeyStore : IAiKeyStore
   29: public AiKeyStore(ILogger<AiKeyStore> logger)
   36: public IReadOnlyCollection<string> ConfiguredProviders
   41: public bool HasKey(string providerId)
   48: public string? Get(string providerId)
   54: public void Set(string providerId, string apiKey)
   64: public void Remove(string providerId)
```

## src/windows/Shell/TradingTerminal.Login/BrokerLoginFormBase.cs
```cs
   23: public abstract class BrokerLoginFormBase : ViewModelBase, IBrokerLoginForm, IDisposable
   27: protected readonly IBrokerSelector Selector;
   28: protected readonly ILogger Logger;
   31: protected BrokerLoginFormBase(IBrokerSelector selector, ILogger logger)
   40: public abstract BrokerKind Broker { get; }
   41: public abstract string DisplayName { get; }
   42: public abstract bool CanSubmit { get; }
   43: public abstract void ApplyToOptions();
   44: public abstract string GetSessionAccountLabel();
   45: public abstract string GetTimeoutErrorMessage();
   46: public abstract string GetFailureMessage();
   47: public abstract void Load();
   48: public abstract void Save();
   51: public ConnectionState CurrentState
   67: public bool IsConnected => CurrentState == ConnectionState.Connected;
   68: public bool IsDisconnected => CurrentState is ConnectionState.Disconnected or ConnectionState.Failed;
   71: public bool IsConnecting
   85: public string? ErrorMessage
   91: public string StatusText => CurrentState switch
  107: public string Badge => Tile.Badge;
  109: public string BadgeColor => Tile.BadgeColor;
  111: public string BadgeForeground => Tile.BadgeForeground;
  113: public string Subtitle => Tile.Subtitle;
  115: public LoginCategory Category => Tile.Category;
  118: public string CategoryName => Category switch
  126: public int CategoryOrder => (int)Category;
  129: public bool IsKeyless => Category == LoginCategory.Keyless;
  133: public bool IsExpanded
  160: public IAsyncRelayCommand ConnectCommand { get; }
  161: public IAsyncRelayCommand DisconnectCommand { get; }
  165: public void Initialize()
  257: public void Dispose()
  267: public enum LoginCategory
```

## src/windows/Shell/TradingTerminal.Login/BrokerLoginFormFactory.cs
```cs
   10: public sealed class BrokerLoginFormFactory : IBrokerLoginFormFactory
   14: public BrokerLoginFormFactory(IEnumerable<IBrokerLoginForm> forms, IBrokerSelector selector)
   23: public IReadOnlyList<IBrokerLoginForm> All { get; }
   25: public IBrokerLoginForm Get(BrokerKind kind)
```

## src/windows/Shell/TradingTerminal.Login/CredentialStore.cs
```cs
    7: public sealed class CredentialStore
   22: public CredentialStore(ILogger<CredentialStore> logger) => _logger = logger;
   24: public StoredCredentials Load()
   40: public void Save(StoredCredentials credentials)
   54: public void Clear()
```

## src/windows/Shell/TradingTerminal.Login/CredentialStoreAiKeyResolver.cs
```cs
   11: public sealed class CredentialStoreAiKeyResolver(AiKeyStore store) : IAiKeyResolver
   15: public string? Resolve(string providerId)
```

## src/windows/Shell/TradingTerminal.Login/Forms/AlpacaLoginForm.xaml.cs
```cs
    6: public partial class AlpacaLoginForm : UserControl
    8: public AlpacaLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/AlpacaLoginFormViewModel.cs
```cs
    9: public sealed class AlpacaLoginFormViewModel : BrokerLoginFormBase
   14: public AlpacaLoginFormViewModel(
   25: public override BrokerKind Broker => BrokerKind.Alpaca;
   26: public override string DisplayName => "Alpaca";
   29: public string Username { get => _username; set => SetProperty(ref _username, value); }
   32: public string ApiKey
   39: public string ApiSecret
   46: public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }
   49: public string StockDataFeed { get => _stockDataFeed; set => SetProperty(ref _stockDataFeed, value); }
   51: public override bool CanSubmit =>
   61: public override void ApplyToOptions()
   69: public override string GetSessionAccountLabel() => IsLive ? "Alpaca · Live" : "Alpaca · Paper";
   71: public override string GetTimeoutErrorMessage() =>
   75: public override string GetFailureMessage() =>
   79: public override void Load()
   89: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/Forms/BinanceLoginForm.xaml.cs
```cs
    5: public partial class BinanceLoginForm : UserControl
    7: public BinanceLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/BinanceLoginFormViewModel.cs
```cs
   13: public sealed class BinanceLoginFormViewModel : BrokerLoginFormBase
   15: public BinanceLoginFormViewModel(
   22: public override BrokerKind Broker => BrokerKind.Binance;
   23: public override string DisplayName => "Binance (no login)";
   26: public override bool CanSubmit => true;
   28: public override void ApplyToOptions() { /* nothing to apply — endpoints come from config defaults */ }
   30: public override string GetSessionAccountLabel() => "Binance · Public data";
   32: public override string GetTimeoutErrorMessage() =>
   36: public override string GetFailureMessage() =>
   41: public override void Load() { /* no persisted credentials */ }
   43: public override void Save() { /* nothing to persist */ }
```

## src/windows/Shell/TradingTerminal.Login/Forms/BybitLoginForm.xaml.cs
```cs
    5: public partial class BybitLoginForm : UserControl
    7: public BybitLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/BybitLoginFormViewModel.cs
```cs
    8: public sealed class BybitLoginFormViewModel : BrokerLoginFormBase
   10: public BybitLoginFormViewModel(IBrokerSelector selector, ILogger<BybitLoginFormViewModel> logger)
   13: public override BrokerKind Broker => BrokerKind.Bybit;
   14: public override string DisplayName => "Bybit (no login)";
   15: public override bool CanSubmit => true;
   16: public override void ApplyToOptions() { }
   17: public override string GetSessionAccountLabel() => "Bybit · Public data";
   18: public override string GetTimeoutErrorMessage() =>
   20: public override string GetFailureMessage() =>
   22: public override void Load() { }
   23: public override void Save() { }
```

## src/windows/Shell/TradingTerminal.Login/Forms/CTraderLoginForm.xaml.cs
```cs
    6: public partial class CTraderLoginForm : UserControl
    8: public CTraderLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/CTraderLoginFormViewModel.cs
```cs
   12: public sealed class CTraderLoginFormViewModel : BrokerLoginFormBase
   18: public CTraderLoginFormViewModel(
   33: public override BrokerKind Broker => BrokerKind.CTrader;
   34: public override string DisplayName => "cTrader";
   37: public string Username { get => _username; set => SetProperty(ref _username, value); }
   40: public string ClientId
   47: public string ClientSecret
   54: public string AccessToken
   61: public long AccountId
   68: public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }
   75: public bool IsDiscovering
   88: public string? DiscoveryMessage
   97: public ObservableCollection<CTraderDiscoveredAccount> DiscoveredAccounts { get; } = new();
  102: public bool HasDiscoveredAccounts => DiscoveredAccounts.Count > 0;
  105: public CTraderDiscoveredAccount? SelectedDiscoveredAccount
  118: public IAsyncRelayCommand DiscoverAccountsCommand { get; }
  120: public override bool CanSubmit =>
  202: public override void ApplyToOptions()
  213: public override string GetSessionAccountLabel() => $"cTrader #{AccountId}";
  215: public override string GetTimeoutErrorMessage() =>
  219: public override string GetFailureMessage() =>
  223: public override void Load()
  234: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/Forms/CoinbaseLoginForm.xaml.cs
```cs
    5: public partial class CoinbaseLoginForm : UserControl
    7: public CoinbaseLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/CoinbaseLoginFormViewModel.cs
```cs
    8: public sealed class CoinbaseLoginFormViewModel : BrokerLoginFormBase
   10: public CoinbaseLoginFormViewModel(IBrokerSelector selector, ILogger<CoinbaseLoginFormViewModel> logger)
   13: public override BrokerKind Broker => BrokerKind.Coinbase;
   14: public override string DisplayName => "Coinbase (no login)";
   15: public override bool CanSubmit => true;
   16: public override void ApplyToOptions() { }
   17: public override string GetSessionAccountLabel() => "Coinbase · Public data";
   18: public override string GetTimeoutErrorMessage() =>
   20: public override string GetFailureMessage() =>
   22: public override void Load() { }
   23: public override void Save() { }
```

## src/windows/Shell/TradingTerminal.Login/Forms/IbLoginForm.xaml.cs
```cs
    6: public partial class IbLoginForm : UserControl
    8: public IbLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/IbLoginFormViewModel.cs
```cs
   12: public sealed class IbLoginFormViewModel : BrokerLoginFormBase
   17: public IbLoginFormViewModel(
   37: public override BrokerKind Broker => BrokerKind.InteractiveBrokers;
   38: public override string DisplayName => "Interactive Brokers";
   40: public IReadOnlyList<string> AccountTypes { get; }
   41: public IReadOnlyList<MarketDataTypeOption> MarketDataTypes { get; }
   44: public string Username { get => _username; set => SetProperty(ref _username, value); }
   47: public string Password { get => _password; set => SetProperty(ref _password, value); }
   50: public string Host { get => _host; set { if (SetProperty(ref _host, value)) RaiseCanSubmit(); } }
   53: public int Port { get => _port; set { if (SetProperty(ref _port, value)) RaiseCanSubmit(); } }
   56: public int ClientId { get => _clientId; set => SetProperty(ref _clientId, value); }
   59: public string AccountType { get => _accountType; set => SetProperty(ref _accountType, value); }
   62: public MarketDataTypeOption? SelectedMarketDataType
   69: public bool RememberPassword { get => _rememberPassword; set => SetProperty(ref _rememberPassword, value); }
   71: public override bool CanSubmit => !string.IsNullOrWhiteSpace(Host) && Port > 0;
   79: public override void ApplyToOptions()
   88: public override string GetSessionAccountLabel() => AccountType;
   90: public override string GetTimeoutErrorMessage() =>
   94: public override string GetFailureMessage() =>
   99: public override void Load()
  113: public override void Save()
  129: public sealed record MarketDataTypeOption(int Value, string DisplayName);
```

## src/windows/Shell/TradingTerminal.Login/Forms/IronBeamLoginForm.xaml.cs
```cs
    6: public partial class IronBeamLoginForm : UserControl
    8: public IronBeamLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/IronBeamLoginFormViewModel.cs
```cs
    9: public sealed class IronBeamLoginFormViewModel : BrokerLoginFormBase
   14: public IronBeamLoginFormViewModel(
   25: public override BrokerKind Broker => BrokerKind.IronBeam;
   26: public override string DisplayName => "Ironbeam";
   29: public string Username
   36: public string ApiKey
   43: public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }
   45: public override bool CanSubmit =>
   55: public override void ApplyToOptions()
   62: public override string GetSessionAccountLabel() =>
   65: public override string GetTimeoutErrorMessage() =>
   69: public override string GetFailureMessage() =>
   73: public override void Load()
   84: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/Forms/KrakenLoginForm.xaml.cs
```cs
    5: public partial class KrakenLoginForm : UserControl
    7: public KrakenLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/KrakenLoginFormViewModel.cs
```cs
    8: public sealed class KrakenLoginFormViewModel : BrokerLoginFormBase
   10: public KrakenLoginFormViewModel(IBrokerSelector selector, ILogger<KrakenLoginFormViewModel> logger)
   13: public override BrokerKind Broker => BrokerKind.Kraken;
   14: public override string DisplayName => "Kraken (no login)";
   15: public override bool CanSubmit => true;
   16: public override void ApplyToOptions() { }
   17: public override string GetSessionAccountLabel() => "Kraken · Public data";
   18: public override string GetTimeoutErrorMessage() =>
   20: public override string GetFailureMessage() =>
   22: public override void Load() { }
   23: public override void Save() { }
```

## src/windows/Shell/TradingTerminal.Login/Forms/LondonStrategicEdgeLoginForm.xaml.cs
```cs
    6: public partial class LondonStrategicEdgeLoginForm : UserControl
    8: public LondonStrategicEdgeLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/LondonStrategicEdgeLoginFormViewModel.cs
```cs
    9: public sealed class LondonStrategicEdgeLoginFormViewModel : BrokerLoginFormBase
   14: public LondonStrategicEdgeLoginFormViewModel(
   25: public override BrokerKind Broker => BrokerKind.LondonStrategicEdge;
   26: public override string DisplayName => "London Strategic Edge";
   29: public string ApiKey
   42: public override bool CanSubmit => !string.IsNullOrWhiteSpace(ApiKey);
   44: public override void ApplyToOptions()
   49: public override string GetSessionAccountLabel() => "London Strategic Edge · Free data";
   51: public override string GetTimeoutErrorMessage() =>
   55: public override string GetFailureMessage() =>
   59: public override void Load()
   67: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/Forms/NinjaLoginForm.xaml.cs
```cs
    5: public partial class NinjaLoginForm : UserControl
    7: public NinjaLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/NinjaLoginFormViewModel.cs
```cs
    9: public sealed class NinjaLoginFormViewModel : BrokerLoginFormBase
   14: public NinjaLoginFormViewModel(
   25: public override BrokerKind Broker => BrokerKind.NinjaTrader;
   26: public override string DisplayName => "NinjaTrader";
   29: public string Username { get => _username; set => SetProperty(ref _username, value); }
   32: public string AccountName
   39: public string DllPath { get => _dllPath; set => SetProperty(ref _dllPath, value); }
   42: public string FuturesContractMonth { get => _futuresContractMonth; set => SetProperty(ref _futuresContractMonth, value); }
   44: public override bool CanSubmit => !string.IsNullOrWhiteSpace(AccountName);
   46: public override void ApplyToOptions()
   53: public override string GetSessionAccountLabel() => AccountName;
   55: public override string GetTimeoutErrorMessage() =>
   58: public override string GetFailureMessage() =>
   62: public override void Load()
   71: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/Forms/OkxLoginForm.xaml.cs
```cs
    5: public partial class OkxLoginForm : UserControl
    7: public OkxLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/OkxLoginFormViewModel.cs
```cs
    8: public sealed class OkxLoginFormViewModel : BrokerLoginFormBase
   10: public OkxLoginFormViewModel(IBrokerSelector selector, ILogger<OkxLoginFormViewModel> logger)
   13: public override BrokerKind Broker => BrokerKind.Okx;
   14: public override string DisplayName => "OKX (no login)";
   15: public override bool CanSubmit => true;
   16: public override void ApplyToOptions() { }
   17: public override string GetSessionAccountLabel() => "OKX · Public data";
   18: public override string GetTimeoutErrorMessage() =>
   20: public override string GetFailureMessage() =>
   22: public override void Load() { }
   23: public override void Save() { }
```

## src/windows/Shell/TradingTerminal.Login/Forms/UpstoxLoginForm.xaml.cs
```cs
    6: public partial class UpstoxLoginForm : UserControl
    8: public UpstoxLoginForm()
```

## src/windows/Shell/TradingTerminal.Login/Forms/UpstoxLoginFormViewModel.cs
```cs
   25: public sealed class UpstoxLoginFormViewModel : BrokerLoginFormBase
   31: public UpstoxLoginFormViewModel(
   47: public override BrokerKind Broker => BrokerKind.Upstox;
   48: public override string DisplayName => "Upstox";
   51: public string ApiKey
   58: public string ApiSecret
   65: public string RedirectUri
   72: public string AuthCode
   79: public string AccessToken
   87: public string? AuthMessage
   94: public bool IsExchanging
  100: public IRelayCommand AuthorizeCommand { get; }
  101: public IAsyncRelayCommand ExchangeCodeCommand { get; }
  103: public override bool CanSubmit => !string.IsNullOrWhiteSpace(AccessToken);
  165: public override void ApplyToOptions()
  173: public override string GetSessionAccountLabel() => "Upstox";
  175: public override string GetTimeoutErrorMessage() =>
  178: public override string GetFailureMessage() =>
  181: public override void Load()
  190: public override void Save()
```

## src/windows/Shell/TradingTerminal.Login/LoginServiceCollectionExtensions.cs
```cs
   23: public static class LoginServiceCollectionExtensions
   27: public static IServiceCollection AddLogin(this IServiceCollection services)
   66: public static IServiceCollection AddCredentialedLoginForms(this IServiceCollection services)
```

## src/windows/Shell/TradingTerminal.Login/LoginViewModel.cs
```cs
   34: public sealed partial class LoginViewModel : ViewModelBase, IDisposable
   52: public LoginViewModel(
  112: public IReadOnlyList<IBrokerLoginForm> AvailableForms { get; }
  116: public ICollectionView FormsView { get; }
  218: public bool CanLaunch => ConnectedCount > 0;
  269: public event EventHandler<bool>? LoginCompleted;
  426: public ObservableCollection<ServiceDependencyViewModel> Services { get; } = new();
  531: public void Dispose()
```

## src/windows/Shell/TradingTerminal.Login/LoginWindow.xaml.cs
```cs
   15: public partial class LoginWindow : MetroWindow
   19: public LoginWindow() => InitializeComponent();
```

## src/windows/Shell/TradingTerminal.Login/ServiceDependencyViewModel.cs
```cs
   11: public enum ServiceState
   32: public sealed partial class ServiceDependencyViewModel : ObservableObject
   37: public ServiceDependencyViewModel(
   58: public string Name { get; }
   59: public string Purpose { get; }
   60: public string Requirement { get; }
   61: public string HowTo { get; }
   62: public string? StartCommand { get; }
   64: public bool HasStartCommand => !string.IsNullOrWhiteSpace(StartCommand);
   65: public bool CanProbe => _probe is not null;
   67: public string StartActionLabel { get; }
   68: public bool HasStartAction => _startAction is not null;
   71: public async Task RunStartAsync(CancellationToken ct = default)
   86: public async Task CheckAsync(CancellationToken ct = default)
  108: public static async Task<bool> HttpOkAsync(string url, CancellationToken ct)
  123: public static async Task<bool> TcpOpenAsync(string host, int[] ports, CancellationToken ct)
  158: public static Task<bool> DockerRunningAsync(CancellationToken ct) => Task.Run(() =>
```

## src/windows/Shell/TradingTerminal.Login/StoredCredentials.cs
```cs
   13: public sealed class StoredCredentials
   16: public BrokerKind SelectedBroker { get; set; } = BrokerKind.InteractiveBrokers;
   20: public bool AutoConnect { get; set; }
   22: public string? Username { get; set; }
   23: public string Host { get; set; } = "127.0.0.1";
   24: public int Port { get; set; } = 7497;
   25: public int ClientId { get; set; } = 1;
   26: public string AccountType { get; set; } = "Paper";
   27: public int MarketDataType { get; set; } = 1;
   28: public bool RememberPassword { get; set; }
   31: public string NinjaAccountName { get; set; } = "Sim101";
   32: public string NinjaDllPath { get; set; } = string.Empty;
   33: public string NinjaFuturesContractMonth { get; set; } = string.Empty;
   36: public string CTraderClientId { get; set; } = string.Empty;
   37: public long CTraderAccountId { get; set; }
   38: public bool CTraderIsLive { get; set; }
   41: public string? CTraderClientSecretEncryptedBase64 { get; set; }
   43: public string? CTraderAccessTokenEncryptedBase64 { get; set; }
   46: public string? CTraderClientSecret
   53: public string? CTraderAccessToken
   60: public string? IronBeamUsername { get; set; }
   61: public bool IronBeamIsLive { get; set; }
   64: public string? IronBeamApiKeyEncryptedBase64 { get; set; }
   67: public string? IronBeamApiKey
   76: public string? LondonStrategicEdgeApiKeyEncryptedBase64 { get; set; }
   79: public string? LondonStrategicEdgeApiKey
   86: public string AlpacaApiKey { get; set; } = string.Empty;
   87: public bool AlpacaIsLive { get; set; }
   88: public string AlpacaStockDataFeed { get; set; } = "iex";
   91: public string? AlpacaApiSecretEncryptedBase64 { get; set; }
   94: public string? AlpacaApiSecret
  101: public string UpstoxApiKey { get; set; } = string.Empty;
  102: public string UpstoxRedirectUri { get; set; } = string.Empty;
  105: public string? UpstoxApiSecretEncryptedBase64 { get; set; }
  108: public string? UpstoxAccessTokenEncryptedBase64 { get; set; }
  111: public string? UpstoxApiSecret
  118: public string? UpstoxAccessToken
  146: public string? PasswordEncryptedBase64 { get; set; }
  149: public string? Password
```
