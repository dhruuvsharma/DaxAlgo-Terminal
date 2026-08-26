using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>Deribit public market data — crypto options and perpetuals, no credentials.</summary>
public sealed class DeribitLoginFormViewModel : BrokerLoginFormBase
{
    private readonly DeribitOptions _options;

    public DeribitLoginFormViewModel(
        IBrokerSelector selector, IOptions<DeribitOptions> options,
        ILogger<DeribitLoginFormViewModel> logger)
        : base(selector, logger) => _options = options.Value;

    public override BrokerKind Broker => BrokerKind.Deribit;
    public override string DisplayName => "Deribit (no login)";
    public override bool CanSubmit => true;

    /// <summary>Drops any stored key, so choosing this row means keyless.</summary>
    public override void ApplyToOptions() => _options.Credentials.Clear();

    public override string GetSessionAccountLabel() => "Deribit · Public data";

    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching Deribit. Check your internet connection.";

    public override string GetFailureMessage() =>
        "Couldn't reach Deribit public market data. Check connectivity or the hosts in appsettings.json.";

    public override void Load() { }
    public override void Save() { }
}
