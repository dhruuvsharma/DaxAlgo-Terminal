using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>Fail-closed configuration for the Interactive Brokers execution adapter.</summary>
public sealed class InteractiveBrokersExecutionOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "InteractiveBrokersExecution";

    /// <summary>Mode-neutral broker identifier used by persisted live confirmations.</summary>
    public const string BrokerId = "interactive-brokers";

    /// <summary>Default loopback host for TWS or IB Gateway.</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>Default TWS paper-trading port.</summary>
    public const int TwsPaperPort = 7497;

    /// <summary>Default IB Gateway paper-trading port.</summary>
    public const int GatewayPaperPort = 4002;

    /// <summary>Default TWS live-trading port.</summary>
    public const int TwsLivePort = 7496;

    /// <summary>Default IB Gateway live-trading port.</summary>
    public const int GatewayLivePort = 4001;

    /// <summary>Explicit opt-in. The default registers no IB execution services.</summary>
    public bool Enabled { get; set; }

    /// <summary>Execution environment. Paper is the immutable safe default.</summary>
    public ExecutionMode Mode { get; set; } = ExecutionMode.Paper;

    /// <summary>Independent owner gate. It defaults false and is required only for live.</summary>
    public bool AllowLiveExecution { get; set; }

    /// <summary>TWS or IB Gateway host supplied from local configuration.</summary>
    public string Host { get; set; } = DefaultHost;

    /// <summary>Mode-specific TWS or IB Gateway port. The default is TWS paper.</summary>
    public int Port { get; set; } = TwsPaperPort;

    /// <summary>Unique TWS API client identifier.</summary>
    public int ClientId { get; set; } = 2;

    /// <summary>Exact IB account identity expected after authentication.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>One exact IB symbol bound to this adapter instance.</summary>
    public string Symbol { get; set; } = "AAPL";

    /// <summary>IB security type such as <c>STK</c>, <c>FUT</c>, <c>OPT</c>, or <c>CASH</c>.</summary>
    public string SecurityType { get; set; } = "STK";

    /// <summary>IB destination exchange; <c>SMART</c> is the default.</summary>
    public string Exchange { get; set; } = "SMART";

    /// <summary>Optional primary exchange used to disambiguate the contract.</summary>
    public string PrimaryExchange { get; set; } = string.Empty;

    /// <summary>Contract currency.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Optional native IB contract identifier; zero requests symbol-based resolution.</summary>
    public int ContractId { get; set; }

    /// <summary>Canonical OMS instrument identifier bound to the configured contract.</summary>
    public int CanonicalInstrumentId { get; set; }

    /// <summary>Whether submitted orders may execute outside regular trading hours.</summary>
    public bool OutsideRegularTradingHours { get; set; }

    /// <summary>Conservative outbound command budget below IB's message-rate limit.</summary>
    public int MaximumCommandsPerSecond { get; set; } = 45;

    /// <summary>Bound applied to native request/response handshakes.</summary>
    public int RequestTimeoutMilliseconds { get; set; } = 10_000;

    /// <summary>Bound on native/client order mappings retained by the adapter.</summary>
    public int MaximumTrackedOrders { get; set; } = 4_096;

    internal InteractiveBrokersExecutionOptions Snapshot() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        AllowLiveExecution = AllowLiveExecution,
        Host = Host,
        Port = Port,
        ClientId = ClientId,
        AccountId = AccountId,
        Symbol = Symbol,
        SecurityType = SecurityType,
        Exchange = Exchange,
        PrimaryExchange = PrimaryExchange,
        Currency = Currency,
        ContractId = ContractId,
        CanonicalInstrumentId = CanonicalInstrumentId,
        OutsideRegularTradingHours = OutsideRegularTradingHours,
        MaximumCommandsPerSecond = MaximumCommandsPerSecond,
        RequestTimeoutMilliseconds = RequestTimeoutMilliseconds,
        MaximumTrackedOrders = MaximumTrackedOrders,
    };

    internal bool HasRequiredLiveCredentials =>
        IsLivePort(Port) &&
        LiveExecutionConfirmation.IsLookupValid(BrokerId, AccountId) &&
        string.Equals(AccountId, AccountId.Trim(), StringComparison.Ordinal);

    internal string? ValidateNonSecretConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            !string.Equals(Host, Host.Trim(), StringComparison.Ordinal))
        {
            return "Host must be between 1 and 253 trimmed characters.";
        }
        if (ClientId < 0)
            return "ClientId cannot be negative.";
        if (!string.IsNullOrEmpty(AccountId) &&
            (!LiveExecutionConfirmation.IsLookupValid(BrokerId, AccountId) ||
             !string.Equals(AccountId, AccountId.Trim(), StringComparison.Ordinal)))
        {
            return $"AccountId must be between 1 and {LiveExecutionConfirmation.MaximumAccountIdLength} trimmed characters.";
        }
        if (!IsBoundedToken(Symbol, 64))
            return "Symbol must be between 1 and 64 trimmed characters.";
        if (!IsBoundedToken(SecurityType, 16))
            return "SecurityType must be between 1 and 16 trimmed characters.";
        if (!IsBoundedToken(Exchange, 32))
            return "Exchange must be between 1 and 32 trimmed characters.";
        if (!string.IsNullOrEmpty(PrimaryExchange) && !IsBoundedToken(PrimaryExchange, 32))
            return "PrimaryExchange must be empty or between 1 and 32 trimmed characters.";
        if (!IsBoundedToken(Currency, 8))
            return "Currency must be between 1 and 8 trimmed characters.";
        if (ContractId < 0)
            return "ContractId cannot be negative.";
        if (CanonicalInstrumentId <= 0)
            return "A positive canonical InstrumentId is required.";
        if (MaximumCommandsPerSecond is < 1 or > 50)
            return "MaximumCommandsPerSecond must be between 1 and 50.";
        if (RequestTimeoutMilliseconds is < 100 or > 60_000)
            return "RequestTimeoutMilliseconds must be between 100 and 60000.";
        if (MaximumTrackedOrders is < 32 or > 65_536)
            return "MaximumTrackedOrders must be between 32 and 65536.";
        return null;
    }

    internal static bool IsPaperPort(int port) => port is TwsPaperPort or GatewayPaperPort;

    internal static bool IsLivePort(int port) => port is TwsLivePort or GatewayLivePort;

    private static bool IsBoundedToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}

/// <summary>Mode-specific endpoint token constructable only through the central authorization gate.</summary>
public sealed record InteractiveBrokersExecutionEndpoint
{
    internal InteractiveBrokersExecutionEndpoint(ExecutionMode mode, string host, int port)
    {
        Mode = mode;
        Host = host;
        Port = port;
    }

    /// <summary>Authorized execution mode.</summary>
    public ExecutionMode Mode { get; }

    /// <summary>Exact configured TWS or Gateway host.</summary>
    public string Host { get; }

    /// <summary>Exact mode-specific TWS or Gateway port.</summary>
    public int Port { get; }

    /// <summary>Gets whether this is an exact paper endpoint.</summary>
    public bool IsPaper =>
        Mode == ExecutionMode.Paper &&
        InteractiveBrokersExecutionOptions.IsPaperPort(Port);

    /// <summary>Gets whether this is an exact live endpoint.</summary>
    public bool IsLive =>
        Mode == ExecutionMode.Live &&
        InteractiveBrokersExecutionOptions.IsLivePort(Port);

    internal bool IsAuthorized =>
        !string.IsNullOrWhiteSpace(Host) &&
        (IsPaper || IsLive);
}

/// <summary>Central endpoint gate shared by registration, adapter, and native transport.</summary>
public static class InteractiveBrokersExecutionEndpointGate
{
    /// <summary>Resolves an exact mode-specific endpoint or throws before a socket can be constructed.</summary>
    public static InteractiveBrokersExecutionEndpoint Resolve(
        InteractiveBrokersExecutionOptions options,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new InvalidOperationException("The Interactive Brokers execution endpoint cannot be resolved without explicit opt-in.");
        if (!Enum.IsDefined(options.Mode))
            throw new InvalidOperationException("The Interactive Brokers execution mode is invalid.");
        if (string.IsNullOrWhiteSpace(options.Host) || options.Host.Length > 253 ||
            !string.Equals(options.Host, options.Host.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Interactive Brokers requires one exact bounded host.");
        }

        var expectedPortClassMatches = options.Mode == ExecutionMode.Live
            ? InteractiveBrokersExecutionOptions.IsLivePort(options.Port)
            : InteractiveBrokersExecutionOptions.IsPaperPort(options.Port);
        if (!expectedPortClassMatches)
        {
            var ports = options.Mode == ExecutionMode.Live
                ? $"{InteractiveBrokersExecutionOptions.TwsLivePort} (TWS) or {InteractiveBrokersExecutionOptions.GatewayLivePort} (Gateway)"
                : $"{InteractiveBrokersExecutionOptions.TwsPaperPort} (TWS) or {InteractiveBrokersExecutionOptions.GatewayPaperPort} (Gateway)";
            throw new InvalidOperationException($"Interactive Brokers {options.Mode} mode requires port {ports}.");
        }

        if (options.Mode == ExecutionMode.Live)
        {
            _ = LiveExecutionAuthorizationGate.Require(
                options.AllowLiveExecution,
                options.HasRequiredLiveCredentials,
                InteractiveBrokersExecutionOptions.BrokerId,
                options.AccountId,
                confirmationStore);
        }

        return new InteractiveBrokersExecutionEndpoint(options.Mode, options.Host, options.Port);
    }
}

/// <summary>Explicit opt-in registrations for the gated Interactive Brokers execution path.</summary>
public static class InteractiveBrokersExecutionServiceCollectionExtensions
{
    /// <summary>Adds the IB adapter only when <see cref="InteractiveBrokersExecutionOptions.Enabled"/> is true.</summary>
    public static IServiceCollection AddInteractiveBrokersExecution(
        this IServiceCollection services,
        Action<InteractiveBrokersExecutionOptions>? configure = null,
        Func<IServiceProvider, InteractiveBrokersExecutionEndpoint, IInteractiveBrokersExecutionTransport>? transportFactory = null,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var mutable = new InteractiveBrokersExecutionOptions();
        configure?.Invoke(mutable);
        if (!mutable.Enabled)
            return services;

        var options = mutable.Snapshot();
        var endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(options, confirmationStore);
        var configurationFault = options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            throw new InvalidOperationException(configurationFault);

        services.AddSingleton(options);
        services.AddSingleton(endpoint);
        services.AddSingleton<InteractiveBrokersSerializedEventScheduler>();
        services.AddSingleton<InteractiveBrokersExecutionAdapter>(provider =>
        {
            var transport = transportFactory?.Invoke(provider, endpoint) ??
                InteractiveBrokersExecutionTransportFactory.CreateDefault(
                    endpoint,
                    TimeSpan.FromMilliseconds(options.RequestTimeoutMilliseconds),
                    options.MaximumTrackedOrders);
            try
            {
                return new InteractiveBrokersExecutionAdapter(
                    provider.GetRequiredService<InteractiveBrokersExecutionOptions>(),
                    transport,
                    provider.GetRequiredService<IClock>(),
                    provider.GetRequiredService<InteractiveBrokersSerializedEventScheduler>(),
                    confirmationStore);
            }
            catch
            {
                try
                {
                    transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Preserve the construction failure; the transport attempted deterministic cleanup.
                }
                throw;
            }
        });
        services.AddSingleton<IBrokerExecutionAdapter>(provider =>
            provider.GetRequiredService<InteractiveBrokersExecutionAdapter>());
        return services;
    }
}
