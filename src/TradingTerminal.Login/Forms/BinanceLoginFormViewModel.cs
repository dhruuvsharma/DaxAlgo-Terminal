using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// Login form for Binance public market data. There are no credentials to enter — Binance's
/// market-data endpoints are public — so this form has no fields and <see cref="CanSubmit"/> is
/// always true. The user just clicks Connect to stream a real, live crypto feed. This is the
/// zero-setup way to try the terminal against real data.
/// </summary>
public sealed class BinanceLoginFormViewModel : BrokerLoginFormBase
{
    public BinanceLoginFormViewModel(
        IBrokerSelector selector,
        ILogger<BinanceLoginFormViewModel> logger)
        : base(selector, logger)
    {
    }

    public override BrokerKind Broker => BrokerKind.Binance;
    public override string DisplayName => "Binance (no login)";

    // No credentials required — public market data.
    public override bool CanSubmit => true;

    public override void ApplyToOptions() { /* nothing to apply — endpoints come from config defaults */ }

    public override string GetSessionAccountLabel() => "Binance · Public data";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Binance. Check your internet connection. If Binance is geo-blocked " +
        "where you are, set Binance:RestBaseUrl / WsBaseUrl to the Binance.US or data-api.binance.vision hosts in appsettings.json.";

    public override string GetFailureMessage() =>
        "Couldn't reach Binance public market data. If Binance is blocked in your region, point " +
        "Binance:RestBaseUrl / WsBaseUrl at https://api.binance.us + wss://stream.binance.us:9443 (or the " +
        "data-api.binance.vision mirror) in appsettings.json.";

    public override void Load() { /* no persisted credentials */ }

    public override void Save() { /* nothing to persist */ }
}
