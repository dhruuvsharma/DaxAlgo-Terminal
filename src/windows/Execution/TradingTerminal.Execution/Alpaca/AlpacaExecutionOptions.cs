using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Alpaca;

/// <summary>Fail-closed configuration for the Alpaca execution adapter.</summary>
public sealed class AlpacaExecutionOptions
{
    public const string SectionName = "AlpacaExecution";
    public const string BrokerId = "alpaca";
    public const string PaperBaseUrl = "https://paper-api.alpaca.markets";
    public const string LiveBaseUrl = "https://api.alpaca.markets";
    public const string DataBaseUrl = "https://data.alpaca.markets";

    /// <summary>Explicit opt-in. The default registers no Alpaca execution services.</summary>
    public bool Enabled { get; set; }

    /// <summary>Execution environment. Paper is the immutable safe default.</summary>
    public ExecutionMode Mode { get; set; } = ExecutionMode.Paper;

    /// <summary>Independent owner gate. It defaults false and is required only for live.</summary>
    public bool AllowLiveExecution { get; set; }

    /// <summary>The mode-specific trading API root.</summary>
    public string BaseUrl { get; set; } = PaperBaseUrl;

    /// <summary>The approved Alpaca market-data root used only for an optional latest trade.</summary>
    public string MarketDataBaseUrl { get; set; } = DataBaseUrl;

    /// <summary>Key supplied by local configuration or at runtime. It is never persisted here.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Secret supplied by local configuration or at runtime. It is never exposed.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Exact Alpaca account ID expected after authentication. It is mandatory for live so the
    /// persisted confirmation and authenticated account are the same binding.
    /// </summary>
    public string ExpectedAccountId { get; set; } = string.Empty;

    /// <summary>One exact Alpaca symbol bound to this adapter instance.</summary>
    public string Symbol { get; set; } = "AAPL";

    /// <summary>Canonical instrument ID bound exactly to <see cref="Symbol"/>.</summary>
    public int CanonicalInstrumentId { get; set; }

    /// <summary>Polling interval for Alpaca order updates.</summary>
    public int PollIntervalMilliseconds { get; set; } = 1_000;

    /// <summary>Bound on order fingerprints retained by the polling source and adapter.</summary>
    public int MaximumTrackedOrders { get; set; } = 4_096;

    /// <summary>Conservative local Trading API command budget.</summary>
    public int MaximumCommandsPerMinute { get; set; } = 200;

    /// <summary>Bound applied to every Trading API request.</summary>
    public int RequestTimeoutMilliseconds { get; set; } = 10_000;

    internal AlpacaExecutionOptions Snapshot() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        AllowLiveExecution = AllowLiveExecution,
        BaseUrl = BaseUrl,
        MarketDataBaseUrl = MarketDataBaseUrl,
        KeyId = KeyId,
        SecretKey = SecretKey,
        ExpectedAccountId = ExpectedAccountId,
        Symbol = Symbol,
        CanonicalInstrumentId = CanonicalInstrumentId,
        PollIntervalMilliseconds = PollIntervalMilliseconds,
        MaximumTrackedOrders = MaximumTrackedOrders,
        MaximumCommandsPerMinute = MaximumCommandsPerMinute,
        RequestTimeoutMilliseconds = RequestTimeoutMilliseconds,
    };

    internal bool HasRequiredCredentials =>
        !string.IsNullOrWhiteSpace(KeyId) &&
        !string.IsNullOrWhiteSpace(SecretKey);

    internal string? ValidateNonSecretConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Symbol) || Symbol.Length > 32)
            return "An Alpaca symbol between 1 and 32 characters is required.";
        if (!string.IsNullOrEmpty(ExpectedAccountId) &&
            (!LiveExecutionConfirmation.IsLookupValid(BrokerId, ExpectedAccountId) ||
             !string.Equals(ExpectedAccountId, ExpectedAccountId.Trim(), StringComparison.Ordinal)))
        {
            return $"ExpectedAccountId must be between 1 and {LiveExecutionConfirmation.MaximumAccountIdLength} trimmed characters.";
        }
        if (CanonicalInstrumentId <= 0)
            return "A positive canonical InstrumentId is required.";
        if (PollIntervalMilliseconds is < 100 or > 60_000)
            return "PollIntervalMilliseconds must be between 100 and 60000.";
        if (MaximumTrackedOrders is < 32 or > 65_536)
            return "MaximumTrackedOrders must be between 32 and 65536.";
        if (MaximumCommandsPerMinute is < 1 or > 10_000)
            return "MaximumCommandsPerMinute must be between 1 and 10000.";
        if (RequestTimeoutMilliseconds is < 100 or > 60_000)
            return "RequestTimeoutMilliseconds must be between 100 and 60000.";
        return null;
    }
}

/// <summary>Mode-specific endpoint token constructable only through the central authorization gate.</summary>
public sealed record AlpacaExecutionEndpoint
{
    internal AlpacaExecutionEndpoint(ExecutionMode mode, Uri tradingBaseUri, Uri marketDataBaseUri)
    {
        Mode = mode;
        TradingBaseUri = tradingBaseUri;
        MarketDataBaseUri = marketDataBaseUri;
    }

    public ExecutionMode Mode { get; }

    public Uri TradingBaseUri { get; }

    public Uri MarketDataBaseUri { get; }

    public bool IsPaper =>
        Mode == ExecutionMode.Paper &&
        string.Equals(TradingBaseUri.AbsoluteUri.TrimEnd('/'), AlpacaExecutionOptions.PaperBaseUrl, StringComparison.Ordinal) &&
        string.Equals(MarketDataBaseUri.AbsoluteUri.TrimEnd('/'), AlpacaExecutionOptions.DataBaseUrl, StringComparison.Ordinal);

    public bool IsLive =>
        Mode == ExecutionMode.Live &&
        string.Equals(TradingBaseUri.AbsoluteUri.TrimEnd('/'), AlpacaExecutionOptions.LiveBaseUrl, StringComparison.Ordinal) &&
        string.Equals(MarketDataBaseUri.AbsoluteUri.TrimEnd('/'), AlpacaExecutionOptions.DataBaseUrl, StringComparison.Ordinal);

    internal bool IsAuthorized => IsPaper || IsLive;
}

/// <summary>Central endpoint gate shared by registration, adapter, and transport.</summary>
public static class AlpacaExecutionEndpointGate
{
    /// <summary>Resolves an exact mode-specific endpoint or throws before a transport can be constructed.</summary>
    public static AlpacaExecutionEndpoint Resolve(
        AlpacaExecutionOptions options,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new InvalidOperationException("The Alpaca execution endpoint cannot be resolved without explicit opt-in.");
        if (!Enum.IsDefined(options.Mode))
            throw new InvalidOperationException("The Alpaca execution mode is invalid.");

        var trading = (options.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var data = (options.MarketDataBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!string.Equals(data, AlpacaExecutionOptions.DataBaseUrl, StringComparison.Ordinal))
            throw new InvalidOperationException($"Only {AlpacaExecutionOptions.DataBaseUrl} is permitted for Alpaca market data.");

        var expectedTrading = options.Mode == ExecutionMode.Live
            ? AlpacaExecutionOptions.LiveBaseUrl
            : AlpacaExecutionOptions.PaperBaseUrl;
        if (!string.Equals(trading, expectedTrading, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Alpaca {options.Mode} mode requires the exact trading endpoint {expectedTrading}.");
        }

        if (options.Mode == ExecutionMode.Live)
        {
            _ = LiveExecutionAuthorizationGate.Require(
                options.AllowLiveExecution,
                options.HasRequiredCredentials,
                AlpacaExecutionOptions.BrokerId,
                options.ExpectedAccountId,
                confirmationStore);
        }

        return new AlpacaExecutionEndpoint(
            options.Mode,
            new Uri(expectedTrading + "/", UriKind.Absolute),
            new Uri(AlpacaExecutionOptions.DataBaseUrl + "/", UriKind.Absolute));
    }
}

/// <summary>Explicit opt-in registrations for the gated Alpaca execution path.</summary>
public static class AlpacaExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddAlpacaExecution(
        this IServiceCollection services,
        Action<AlpacaExecutionOptions>? configure = null,
        Func<IServiceProvider, AlpacaExecutionEndpoint, IAlpacaExecutionTransport>? transportFactory = null,
        Func<IServiceProvider, IAlpacaTradeUpdateSource>? updateSourceFactory = null,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var mutable = new AlpacaExecutionOptions();
        configure?.Invoke(mutable);
        if (!mutable.Enabled)
            return services;

        var options = mutable.Snapshot();
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, confirmationStore);
        var configurationFault = options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            throw new InvalidOperationException(configurationFault);

        services.AddSingleton(options);
        services.AddSingleton(endpoint);
        services.AddSingleton<AlpacaSerializedEventScheduler>();
        services.AddSingleton<IAlpacaTradeUpdateSource>(provider =>
            updateSourceFactory?.Invoke(provider) ??
            new AlpacaPollingTradeUpdateSource(
                TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds),
                options.MaximumTrackedOrders));
        services.AddSingleton<AlpacaExecutionAdapter>(provider =>
        {
            var transport = transportFactory?.Invoke(provider, endpoint) ??
                new AlpacaHttpExecutionTransport(
                    endpoint,
                    TimeSpan.FromMilliseconds(options.RequestTimeoutMilliseconds));
            try
            {
                return new AlpacaExecutionAdapter(
                    provider.GetRequiredService<AlpacaExecutionOptions>(),
                    transport,
                    provider.GetRequiredService<IAlpacaTradeUpdateSource>(),
                    provider.GetRequiredService<IClock>(),
                    provider.GetRequiredService<AlpacaSerializedEventScheduler>(),
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
        services.AddSingleton<IBrokerExecutionAdapter>(provider => provider.GetRequiredService<AlpacaExecutionAdapter>());
        return services;
    }
}
