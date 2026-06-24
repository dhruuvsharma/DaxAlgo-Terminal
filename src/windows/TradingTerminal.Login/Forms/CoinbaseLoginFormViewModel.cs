using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>Login form for Coinbase public market data — no credentials (keyless, like Binance).</summary>
public sealed class CoinbaseLoginFormViewModel : BrokerLoginFormBase
{
    public CoinbaseLoginFormViewModel(IBrokerSelector selector, ILogger<CoinbaseLoginFormViewModel> logger)
        : base(selector, logger) { }

    public override BrokerKind Broker => BrokerKind.Coinbase;
    public override string DisplayName => "Coinbase (no login)";
    public override bool CanSubmit => true;
    public override void ApplyToOptions() { }
    public override string GetSessionAccountLabel() => "Coinbase · Public data";
    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Coinbase. Check your internet connection.";
    public override string GetFailureMessage() =>
        "Couldn't reach Coinbase public market data. Check connectivity or the Coinbase hosts in appsettings.json.";
    public override void Load() { }
    public override void Save() { }
}
