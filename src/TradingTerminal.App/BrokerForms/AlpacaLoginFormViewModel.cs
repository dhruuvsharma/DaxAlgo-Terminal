using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login.Forms;

public sealed class AlpacaLoginFormViewModel : ViewModelBase, IBrokerLoginForm
{
    private readonly AlpacaOptions _options;
    private readonly CredentialStore _credentialStore;

    public AlpacaLoginFormViewModel(IOptions<AlpacaOptions> options, CredentialStore credentialStore)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public BrokerKind Broker => BrokerKind.Alpaca;
    public string DisplayName => "Alpaca";

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _apiKey = string.Empty;
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }

    private string _apiSecret = string.Empty;
    public string ApiSecret { get => _apiSecret; set => SetProperty(ref _apiSecret, value); }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }

    private string _stockDataFeed = "iex";
    public string StockDataFeed { get => _stockDataFeed; set => SetProperty(ref _stockDataFeed, value); }

    public bool CanSubmit =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ApiSecret);

    public void ApplyToOptions()
    {
        _options.ApiKey = ApiKey?.Trim() ?? string.Empty;
        _options.ApiSecret = ApiSecret?.Trim() ?? string.Empty;
        _options.IsLive = IsLive;
        _options.StockDataFeed = string.IsNullOrWhiteSpace(StockDataFeed) ? "iex" : StockDataFeed.Trim();
    }

    public string GetSessionAccountLabel() => IsLive ? "Alpaca · Live" : "Alpaca · Paper";

    public string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s reaching {(IsLive ? "api" : "paper-api")}.alpaca.markets. " +
        "Check your internet connection and that the API key + secret are valid.";

    public string GetFailureMessage() =>
        "Alpaca connection failed. Verify your API key and secret are correct and active. " +
        "Generate keys at https://app.alpaca.markets (paper) or https://app.alpaca.markets/live (funded).";

    public void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        ApiKey = stored.AlpacaApiKey;
        ApiSecret = stored.AlpacaApiSecret ?? string.Empty;
        IsLive = stored.AlpacaIsLive;
        StockDataFeed = string.IsNullOrWhiteSpace(stored.AlpacaStockDataFeed) ? "iex" : stored.AlpacaStockDataFeed;
    }

    public void Save()
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
