using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

public sealed class IronBeamLoginFormViewModel : BrokerLoginFormBase
{
    private readonly IronBeamOptions _options;
    private readonly CredentialStore _credentialStore;

    public IronBeamLoginFormViewModel(
        IOptions<IronBeamOptions> options,
        CredentialStore credentialStore,
        IBrokerSelector selector,
        ILogger<IronBeamLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public override BrokerKind Broker => BrokerKind.IronBeam;
    public override string DisplayName => "Ironbeam";

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set { if (SetProperty(ref _username, value)) RaiseCanSubmit(); }
    }

    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set { if (SetProperty(ref _apiKey, value)) RaiseCanSubmit(); }
    }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => SetProperty(ref _isLive, value); }

    public override bool CanSubmit =>
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(ApiKey);

    private void RaiseCanSubmit()
    {
        OnPropertyChanged(nameof(CanSubmit));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public override void ApplyToOptions()
    {
        _options.Username = Username?.Trim() ?? string.Empty;
        _options.ApiKey = ApiKey?.Trim() ?? string.Empty;
        _options.IsLive = IsLive;
    }

    public override string GetSessionAccountLabel() =>
        IsLive ? "Ironbeam · Live" : "Ironbeam · Demo";

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out after 15s reaching {(IsLive ? "live" : "demo")}.ironbeamapi.com. " +
        "Check your internet connection and that your username and API key are correct.";

    public override string GetFailureMessage() =>
        "Ironbeam connection failed. Verify your username and API key at docs.ironbeamapi.com. " +
        "Demo accounts connect to demo.ironbeamapi.com; funded accounts to live.ironbeamapi.com.";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.IronBeamUsername ?? string.Empty;
        IsLive = stored.IronBeamIsLive;
        // ApiKey is write-only on load (only populated when non-empty ciphertext is stored)
        var key = stored.IronBeamApiKey;
        if (!string.IsNullOrEmpty(key))
            ApiKey = key;
    }

    public override void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.IronBeam;
        stored.IronBeamUsername = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        stored.IronBeamIsLive = IsLive;
        if (!string.IsNullOrEmpty(ApiKey)) stored.IronBeamApiKey = ApiKey;
        _credentialStore.Save(stored);
    }
}
