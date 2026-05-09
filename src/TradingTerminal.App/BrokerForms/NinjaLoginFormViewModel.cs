using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.UI;

namespace TradingTerminal.App.Login.Forms;

public sealed class NinjaLoginFormViewModel : ViewModelBase, IBrokerLoginForm
{
    private readonly NinjaTraderOptions _options;
    private readonly CredentialStore _credentialStore;

    public NinjaLoginFormViewModel(IOptions<NinjaTraderOptions> options, CredentialStore credentialStore)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
    }

    public BrokerKind Broker => BrokerKind.NinjaTrader;
    public string DisplayName => "NinjaTrader";

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _accountName = "Sim101";
    public string AccountName { get => _accountName; set => SetProperty(ref _accountName, value); }

    private string _dllPath = string.Empty;
    public string DllPath { get => _dllPath; set => SetProperty(ref _dllPath, value); }

    private string _futuresContractMonth = string.Empty;
    public string FuturesContractMonth { get => _futuresContractMonth; set => SetProperty(ref _futuresContractMonth, value); }

    public bool CanSubmit => !string.IsNullOrWhiteSpace(AccountName);

    public void ApplyToOptions()
    {
        _options.AccountName = string.IsNullOrWhiteSpace(AccountName) ? "Sim101" : AccountName.Trim();
        _options.DllPath = DllPath?.Trim() ?? string.Empty;
        _options.DefaultFuturesContractMonth = FuturesContractMonth?.Trim() ?? string.Empty;
    }

    public string GetSessionAccountLabel() => AccountName;

    public string GetTimeoutErrorMessage() =>
        "Connection timed out after 15s. Verify NinjaTrader 8 is running and the AT Interface is enabled in Tools → Options.";

    public string GetFailureMessage() =>
        "NinjaTrader reported a connection failure. Make sure NinjaTrader 8 is running, signed in, " +
        "and that Tools → Options → AT Interface → 'AT Interface enabled' is checked.";

    public void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        AccountName = string.IsNullOrWhiteSpace(stored.NinjaAccountName) ? "Sim101" : stored.NinjaAccountName;
        DllPath = stored.NinjaDllPath;
        FuturesContractMonth = stored.NinjaFuturesContractMonth;
    }

    public void Save()
    {
        var stored = _credentialStore.Load();
        stored.SelectedBroker = BrokerKind.NinjaTrader;
        stored.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        stored.NinjaAccountName = string.IsNullOrWhiteSpace(AccountName) ? "Sim101" : AccountName.Trim();
        stored.NinjaDllPath = DllPath?.Trim() ?? string.Empty;
        stored.NinjaFuturesContractMonth = FuturesContractMonth?.Trim() ?? string.Empty;
        _credentialStore.Save(stored);
    }
}
