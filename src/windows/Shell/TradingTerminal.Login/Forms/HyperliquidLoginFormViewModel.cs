using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// Hyperliquid public market data.
///
/// <para>Only one row for this venue, unlike the exchanges that appear twice. Hyperliquid's keys
/// authorise trading; they buy nothing for reading, so a keyed row would ask for a secret and give
/// nothing back for it.</para>
/// </summary>
public sealed class HyperliquidLoginFormViewModel : BrokerLoginFormBase
{
    public HyperliquidLoginFormViewModel(
        IBrokerSelector selector, ILogger<HyperliquidLoginFormViewModel> logger)
        : base(selector, logger) { }

    public override BrokerKind Broker => BrokerKind.Hyperliquid;
    public override string DisplayName => "Hyperliquid (no login)";
    public override bool CanSubmit => true;
    public override void ApplyToOptions() { }
    public override string GetSessionAccountLabel() => "Hyperliquid · Public data";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Hyperliquid. Check your internet connection.";

    public override string GetFailureMessage() =>
        "Couldn't reach Hyperliquid public market data. Check connectivity or the hosts in appsettings.json.";

    public override void Load() { }
    public override void Save() { }
}
