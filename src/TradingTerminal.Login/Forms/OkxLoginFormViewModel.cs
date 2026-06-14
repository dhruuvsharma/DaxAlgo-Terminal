using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>Login form for OKX public market data — no credentials (keyless, like Binance).</summary>
public sealed class OkxLoginFormViewModel : BrokerLoginFormBase
{
    public OkxLoginFormViewModel(IBrokerSelector selector, ILogger<OkxLoginFormViewModel> logger)
        : base(selector, logger) { }

    public override BrokerKind Broker => BrokerKind.Okx;
    public override string DisplayName => "OKX (no login)";
    public override bool CanSubmit => true;
    public override void ApplyToOptions() { }
    public override string GetSessionAccountLabel() => "OKX · Public data";
    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching OKX. Check your internet connection.";
    public override string GetFailureMessage() =>
        "Couldn't reach OKX public market data. Check connectivity or the OKX hosts in appsettings.json.";
    public override void Load() { }
    public override void Save() { }
}
