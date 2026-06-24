using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

public sealed class LondonStrategicEdgeLoginFormViewModel : BrokerLoginFormBase
{
    private readonly LondonStrategicEdgeOptions _options;
    private readonly CredentialStore _credentialStore;

    public LondonStrategicEdgeLoginFormViewModel(
        IOptions<LondonStrategicEdgeOptions> options,
        CredentialStore credentialStore,
        IBrokerSelector selector,
        ILogger<LondonStrategicEdgeLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public override BrokerKind Broker => BrokerKind.LondonStrategicEdge;
    public override string DisplayName => "London Strategic Edge";

    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                ConnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public override bool CanSubmit => !string.IsNullOrWhiteSpace(ApiKey);

    public override void ApplyToOptions()
    {
        _options.ApiKey = ApiKey?.Trim() ?? string.Empty;
    }

    public override string GetSessionAccountLabel() => "London Strategic Edge · Free data";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out after 15s reaching data-ws.londonstrategicedge.com. " +
        "Check your internet connection and that your API key is correct.";

    public override string GetFailureMessage() =>
        "London Strategic Edge connection failed. Verify your API key at londonstrategicedge.com/websockets " +
        "(keys look like lse_live_…). A QUOTA_EXCEEDED error means the 50 GB/month free tier is exhausted.";

    public override void Load()
    {
        // ApiKey is write-only on load (only populated when non-empty ciphertext is stored)
        var key = _credentialStore.Load().LondonStrategicEdgeApiKey;
        if (!string.IsNullOrEmpty(key))
            ApiKey = key;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.LondonStrategicEdge;
        if (!string.IsNullOrEmpty(ApiKey)) stored.LondonStrategicEdgeApiKey = ApiKey;
        _credentialStore.Save(stored);
    }
}
