using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

public sealed class NinjaLoginFormViewModel : BrokerLoginFormBase
{
    private readonly NinjaTraderOptions _options;
    private readonly CredentialStore _credentialStore;
    private readonly ServiceDependencyViewModel _prerequisite;

    public NinjaLoginFormViewModel(
        IOptions<NinjaTraderOptions> options,
        CredentialStore credentialStore,
        IBrokerSelector selector,
        ILogger<NinjaLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _options = options.Value;
        _credentialStore = credentialStore;

        // NTDirect is a P/Invoke bridge into the running NinjaTrader process — no socket to probe, so
        // the presence of the process is the signal. Lives on this form, not the shell services panel.
        _prerequisite = new ServiceDependencyViewModel(
            name: "NinjaTrader 8 (NTDirect)",
            purpose: "The NinjaTrader feed is a bridge into the desktop app — it must be running and signed in before Connect works.",
            requirement: "Required",
            howTo: "Start NinjaTrader 8, sign in, and enable Tools → Options → AT Interface → “AT Interface enabled”. " +
                   "NTDirect.dll auto-resolves from %USERPROFILE%\\Documents\\NinjaTrader 8\\bin64\\ — override the path " +
                   "under Connection settings below if yours is installed elsewhere.",
            startCommand: null,
            probe: ct => ServiceDependencyViewModel.ProcessRunningAsync("NinjaTrader", ct));
    }

    public override BrokerKind Broker => BrokerKind.NinjaTrader;
    public override string DisplayName => "NinjaTrader";

    /// <summary>NinjaTrader 8 must be up — surfaced inside this row (see the base class).</summary>
    public override ServiceDependencyViewModel? Prerequisite => _prerequisite;

    private string _username = string.Empty;
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _accountName = "Sim101";
    public string AccountName
    {
        get => _accountName;
        set { if (SetProperty(ref _accountName, value)) { OnPropertyChanged(nameof(CanSubmit)); ConnectCommand.NotifyCanExecuteChanged(); } }
    }

    private string _dllPath = string.Empty;
    public string DllPath { get => _dllPath; set => SetProperty(ref _dllPath, value); }

    private string _futuresContractMonth = string.Empty;
    public string FuturesContractMonth { get => _futuresContractMonth; set => SetProperty(ref _futuresContractMonth, value); }

    public override bool CanSubmit => !string.IsNullOrWhiteSpace(AccountName);

    public override void ApplyToOptions()
    {
        _options.AccountName = string.IsNullOrWhiteSpace(AccountName) ? "Sim101" : AccountName.Trim();
        _options.DllPath = DllPath?.Trim() ?? string.Empty;
        _options.DefaultFuturesContractMonth = FuturesContractMonth?.Trim() ?? string.Empty;
    }

    public override string GetSessionAccountLabel() => AccountName;

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out after 15s. Verify NinjaTrader 8 is running and the AT Interface is enabled in Tools → Options.";

    public override string GetFailureMessage() =>
        "NinjaTrader reported a connection failure. Make sure NinjaTrader 8 is running, signed in, " +
        "and that Tools → Options → AT Interface → 'AT Interface enabled' is checked.";

    public override void Load()
    {
        var stored = _credentialStore.Load();
        Username = stored.Username ?? string.Empty;
        AccountName = string.IsNullOrWhiteSpace(stored.NinjaAccountName) ? "Sim101" : stored.NinjaAccountName;
        DllPath = stored.NinjaDllPath;
        FuturesContractMonth = stored.NinjaFuturesContractMonth;
    }

    public override void Save()
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
