using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

public sealed class AlpacaLoginFormViewModel : BrokerLoginFormBase
{
    private readonly AlpacaOptions _options;
    private readonly CredentialStore _credentialStore;

    public AlpacaLoginFormViewModel(
        IOptions<AlpacaOptions> options,
        CredentialStore credentialStore,
        IBrokerSelector selector,
        ILogger<AlpacaLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public override BrokerKind Broker => BrokerKind.Alpaca;
    public override string DisplayName => "Alpaca";

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) RaiseCanSubmit(); }
    }

    private string _apiSecret = string.Empty;
    public string ApiSecret
    {
        get => _apiSecret;
        set { if (SetProperty(ref _apiSecret, value)) RaiseCanSubmit(); }
    }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }

    private string _stockDataFeed = "iex";
    public string StockDataFeed { get => _stockDataFeed; set => SetProperty(ref _stockDataFeed, value); }

    public override bool CanSubmit =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ApiSecret);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void ApplyToOptions()
    {
        _options.ApiKey = ApiKey?.Trim() ?? string.Empty;
        _options.ApiSecret = ApiSecret?.Trim() ?? string.Empty;
        _options.IsLive = IsLive;
        _options.StockDataFeed = string.IsNullOrWhiteSpace(StockDataFeed) ? "iex" : StockDataFeed.Trim();
    }

    public override string GetSessionAccountLabel() => IsLive ? "Alpaca · Live" : "Alpaca · Paper";

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s reaching {(IsLive ? "api" : "paper-api")}.alpaca.markets. " +
        "Check your internet connection and that the API key + secret are valid.";

    public override string GetFailureMessage() =>
        "Alpaca connection failed. Verify your API key and secret are correct and active. " +
        "Generate keys at https://app.alpaca.markets (paper) or https://app.alpaca.markets/live (funded).";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        ApiKey = stored.AlpacaApiKey;
        ApiSecret = stored.AlpacaApiSecret ?? string.Empty;
        IsLive = stored.AlpacaIsLive;
        StockDataFeed = string.IsNullOrWhiteSpace(stored.AlpacaStockDataFeed) ? "iex" : stored.AlpacaStockDataFeed;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.Alpaca;
        stored.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        stored.AlpacaApiKey = ApiKey?.Trim() ?? string.Empty;
        stored.AlpacaIsLive = IsLive;
        stored.AlpacaStockDataFeed = StockDataFeed?.Trim() ?? "iex";
        if (!string.IsNullOrEmpty(ApiSecret)) stored.AlpacaApiSecret = ApiSecret;
        _credentialStore.Save(stored);
    }
}
