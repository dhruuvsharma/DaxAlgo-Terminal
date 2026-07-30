using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Developer-only switches, bound from the <c>Dev</c> configuration section. These are off by
/// default in the shipped <c>appsettings.json</c> and only turned on by the per-environment dev
/// files (<c>appsettings.Dev*.json</c>)
/// selected via the launch profiles' <c>DOTNET_ENVIRONMENT</c>. Never enable in a release build.
/// </summary>
public sealed class DevOptions
{
    public const string SectionName = "Dev";

    /// <summary>
    /// When true, the app skips the login window on startup, auto-connects
    /// <see cref="AutoConnectBrokers"/>, and opens the main shell directly. Tightens the
    /// debug loop when the login + broker handshake is already settled.
    /// </summary>
    public bool BypassLogin { get; set; }

    /// <summary>
    /// Brokers to connect automatically when <see cref="BypassLogin"/> is set. Each is started
    /// through the same <c>IBrokerSelector.ConnectAsync</c> the login forms use; a connect that
    /// fails (e.g. no saved credentials) is logged and skipped, never fatal. Empty by default.
    /// </summary>
    public BrokerKind[] AutoConnectBrokers { get; set; } = [];

    /// <summary>
    /// Clears the locally stored product-account session before the account gate opens, forcing a
    /// fresh interactive sign-in. Intended for signup and first-run testing only.
    /// </summary>
    public bool ResetAccountOnStart { get; set; }

    /// <summary>
    /// Prevents runtime strategy plugins from loading while keeping the empty host catalog
    /// resolvable. Intended for validating the terminal's no-strategies state.
    /// </summary>
    public bool DisableStrategyPlugins { get; set; }
}
