using Microsoft.Extensions.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>Login form for OKX public market data — no credentials (keyless, like Binance).</summary>
public sealed class OkxLoginFormViewModel : BrokerLoginFormBase
{
    private readonly OkxOptions _options;

    public OkxLoginFormViewModel(
        IBrokerSelector selector,
        Microsoft.Extensions.Options.IOptions<OkxOptions> options,
        ILogger<OkxLoginFormViewModel> logger)
        : base(selector, logger) => _options = options.Value;

    public override BrokerKind Broker => BrokerKind.Okx;
    public override string DisplayName => "OKX (no login)";
    public override bool CanSubmit => true;
    /// <summary>Drops any stored key, so choosing this row means keyless rather than "authenticated
    /// because you once pasted a key into the other row".</summary>
    public override void ApplyToOptions() => _options.Credentials.Clear();
    public override string GetSessionAccountLabel() => "OKX · Public data";
    public override string GetTimeoutErrorMessage() =>
        "Connection timed out reaching OKX. Check your internet connection.";
    public override string GetFailureMessage() =>
        "Couldn't reach OKX public market data. Check connectivity or the OKX hosts in appsettings.json.";
    public override void Load() { }
    public override void Save() { }
}
