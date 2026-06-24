using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>Login form for Bybit public market data — no credentials (keyless, like Binance).</summary>
public sealed class BybitLoginFormViewModel : BrokerLoginFormBase
{
    public BybitLoginFormViewModel(IBrokerSelector selector, ILogger<BybitLoginFormViewModel> logger)
        : base(selector, logger) { }

    public override BrokerKind Broker => BrokerKind.Bybit;
    public override string DisplayName => "Bybit (no login)";
    public override bool CanSubmit => true;
    public override void ApplyToOptions() { }
    public override string GetSessionAccountLabel() => "Bybit · Public data";
    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Bybit. Check your internet connection.";
    public override string GetFailureMessage() =>
        "Couldn't reach Bybit public market data. Check connectivity or the Bybit hosts in appsettings.json.";
    public override void Load() { }
    public override void Save() { }
}
