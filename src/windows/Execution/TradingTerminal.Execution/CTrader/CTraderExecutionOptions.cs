using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.CTrader;

/// <summary>Fail-closed configuration for the cTrader execution adapter.</summary>
public sealed class CTraderExecutionOptions
{
    /// <summary>Configuration section read by an explicitly opted-in host.</summary>
    public const string SectionName = "CTraderExecution";

    /// <summary>Mode-neutral broker identity used by persisted live confirmations.</summary>
    public const string BrokerId = "ctrader-openapi";

    /// <summary>The paper/demo endpoint.</summary>
    public const string DemoHost = "demo.ctraderapi.com";

    /// <summary>The real-money endpoint.</summary>
    public const string LiveHost = "live.ctraderapi.com";

    /// <summary>The Spotware TLS/protobuf port used by Open API 2.0.</summary>
    public const int OpenApiPort = 5035;

    /// <summary>Explicit opt-in. The default registers no cTrader execution services.</summary>
    public bool Enabled { get; set; }

    /// <summary>Execution environment. Paper is the immutable safe default.</summary>
    public ExecutionMode Mode { get; set; } = ExecutionMode.Paper;

    /// <summary>Independent owner gate. It defaults false and is required only for live.</summary>
    public bool AllowLiveExecution { get; set; }

    /// <summary>Mode-specific endpoint host.</summary>
    public string Host { get; set; } = DemoHost;

    /// <summary>Endpoint port. Only <see cref="OpenApiPort"/> is accepted.</summary>
    public int Port { get; set; } = OpenApiPort;

    /// <summary>OAuth application client ID supplied by local configuration or environment.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth application client secret supplied by local configuration or environment.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth access token supplied by local configuration or environment.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Explicit mode-specific ctidTraderAccountId. Account auto-selection is intentionally absent.</summary>
    public long CtidTraderAccountId { get; set; }

    /// <summary>One configured cTrader symbol ID; slice 3 exposes one capability set per adapter.</summary>
    public long SymbolId { get; set; }

    /// <summary>Canonical InstrumentId value bound exactly to <see cref="SymbolId"/>.</summary>
    public int CanonicalInstrumentId { get; set; }

    /// <summary>Conservative local Open API command budget.</summary>
    public int MaximumCommandsPerSecond { get; set; } = 5;

    /// <summary>Bound for every correlated Open API request.</summary>
    public int RequestTimeoutMilliseconds { get; set; } = 10_000;

    /// <summary>Bounded completed-order window used by reconciliation snapshots.</summary>
    public int CompletedOrderLookbackDays { get; set; } = 7;

    internal CTraderExecutionOptions Snapshot() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        AllowLiveExecution = AllowLiveExecution,
        Host = Host,
        Port = Port,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        AccessToken = AccessToken,
        CtidTraderAccountId = CtidTraderAccountId,
        SymbolId = SymbolId,
        CanonicalInstrumentId = CanonicalInstrumentId,
        MaximumCommandsPerSecond = MaximumCommandsPerSecond,
        RequestTimeoutMilliseconds = RequestTimeoutMilliseconds,
        CompletedOrderLookbackDays = CompletedOrderLookbackDays,
    };

    internal bool HasRequiredCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(AccessToken) &&
        CtidTraderAccountId > 0;

    internal string? ValidateNonSecretConfiguration()
    {
        if (SymbolId <= 0)
            return "A positive cTrader SymbolId is required.";
        if (CanonicalInstrumentId <= 0)
            return "A positive canonical InstrumentId is required.";
        if (MaximumCommandsPerSecond is <= 0 or > 50)
            return "MaximumCommandsPerSecond must be between 1 and 50.";
        if (RequestTimeoutMilliseconds is < 100 or > 60_000)
            return "RequestTimeoutMilliseconds must be between 100 and 60000.";
        if (CompletedOrderLookbackDays is < 1 or > 7)
            return "CompletedOrderLookbackDays must be between 1 and 7.";
        return null;
    }
}

/// <summary>An endpoint token produced only by the central mode/authorization resolver.</summary>
public sealed record CTraderExecutionEndpoint
{
    internal CTraderExecutionEndpoint(ExecutionMode mode, string host, int port)
    {
        Mode = mode;
        Host = host;
        Port = port;
    }

    /// <summary>The selected paper/live environment.</summary>
    public ExecutionMode Mode { get; }

    /// <summary>The exact gated DNS host.</summary>
    public string Host { get; }

    /// <summary>The exact gated TLS port.</summary>
    public int Port { get; }

    /// <summary>Gets whether this is the exact paper/demo endpoint.</summary>
    public bool IsDemo =>
        Mode == ExecutionMode.Paper &&
        string.Equals(Host, CTraderExecutionOptions.DemoHost, StringComparison.Ordinal) &&
        Port == CTraderExecutionOptions.OpenApiPort;

    /// <summary>Gets whether this is the exact real-money endpoint.</summary>
    public bool IsLive =>
        Mode == ExecutionMode.Live &&
        string.Equals(Host, CTraderExecutionOptions.LiveHost, StringComparison.Ordinal) &&
        Port == CTraderExecutionOptions.OpenApiPort;

    internal bool IsAuthorized => IsDemo || IsLive;
}

/// <summary>Central mode/authorization endpoint gate shared by DI, adapter, and transport.</summary>
public static class CTraderExecutionEndpointGate
{
    /// <summary>Resolves the exact selected endpoint or throws before any transport can be constructed.</summary>
    public static CTraderExecutionEndpoint Resolve(
        CTraderExecutionOptions options,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            throw new InvalidOperationException("The cTrader execution endpoint cannot be resolved without explicit opt-in.");
        if (!Enum.IsDefined(options.Mode))
            throw new InvalidOperationException("The cTrader execution mode is invalid.");
        var host = options.Host?.Trim() ?? string.Empty;
        var expectedHost = options.Mode == ExecutionMode.Live
            ? CTraderExecutionOptions.LiveHost
            : CTraderExecutionOptions.DemoHost;
        if (!string.Equals(host, expectedHost, StringComparison.Ordinal) ||
            options.Port != CTraderExecutionOptions.OpenApiPort)
        {
            throw new InvalidOperationException(
                $"cTrader {options.Mode} mode requires the exact endpoint {expectedHost}:{CTraderExecutionOptions.OpenApiPort}.");
        }

        if (options.Mode == ExecutionMode.Live)
        {
            _ = LiveExecutionAuthorizationGate.Require(
                options.AllowLiveExecution,
                options.HasRequiredCredentials,
                CTraderExecutionOptions.BrokerId,
                options.CtidTraderAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                confirmationStore);
        }

        return new CTraderExecutionEndpoint(options.Mode, expectedHost, CTraderExecutionOptions.OpenApiPort);
    }
}

/// <summary>Single-consumer FIFO production scheduler; tests inject the deterministic scheduler.</summary>
public sealed class CTraderSerializedEventScheduler : IAdapterEventScheduler, IAsyncDisposable, IDisposable
{
    // Bounded so a stalled consumer cannot balloon RAM. Capacity is generous because execution
    // events (acks/fills) are low-rate and the single reader drains immediately; execution events
    // must never be silently dropped, so a (pathological) full queue faults rather than DropOldest,
    // and the sync writer never blocks the transport read loop.
    private const int CallbackCapacity = 4096;
    private readonly Channel<Action> _callbacks = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(CallbackCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _consumer;
    private volatile bool _completed;
    private Exception? _lastCallbackFault;

    /// <summary>Starts the one ordered adapter-event consumer.</summary>
    public CTraderSerializedEventScheduler() => _consumer = Task.Run(ConsumeAsync);

    /// <summary>The last contained subscriber/callback fault, if any.</summary>
    public Exception? LastCallbackFault => Volatile.Read(ref _lastCallbackFault);

    /// <summary>Raised after an ordered callback fails so the adapter can revoke certification.</summary>
    public event Action<Exception>? CallbackFaulted;

    /// <inheritdoc />
    public void Schedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_callbacks.Writer.TryWrite(callback))
            return;
        if (_completed)
            throw new ObjectDisposedException(nameof(CTraderSerializedEventScheduler));
        // Bounded queue full: an execution event must not be dropped — surface a fatal fault.
        var overflow = new InvalidOperationException(
            "cTrader execution-event queue overflow; the ordered callback consumer fell behind.");
        Volatile.Write(ref _lastCallbackFault, overflow);
        CallbackFaulted?.Invoke(overflow);
        throw overflow;
    }

    /// <summary>Best-effort synchronous shutdown: stops accepting callbacks; the consumer drains and
    /// exits on its own. Prefer <see cref="DisposeAsync"/> for deterministic teardown.</summary>
    public void Dispose()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _completed = true;
        _callbacks.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var callback in _callbacks.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                callback();
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastCallbackFault, exception);
                CallbackFaulted?.Invoke(exception);
            }
        }
    }
}

/// <summary>Explicit opt-in registrations for the cTrader execution path.</summary>
public static class CTraderExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers nothing unless <see cref="CTraderExecutionOptions.Enabled"/> is explicitly true.
    /// The optional factory exists so tests and owners can inject a non-network transport.
    /// </summary>
    public static IServiceCollection AddCTraderExecution(
        this IServiceCollection services,
        Action<CTraderExecutionOptions>? configure = null,
        Func<IServiceProvider, CTraderExecutionEndpoint, ICTraderExecutionTransport>? transportFactory = null,
        ILiveExecutionConfirmationStore? confirmationStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var mutable = new CTraderExecutionOptions();
        configure?.Invoke(mutable);
        if (!mutable.Enabled)
            return services;

        var options = mutable.Snapshot();
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, confirmationStore);
        var configurationFault = options.ValidateNonSecretConfiguration();
        if (configurationFault is not null)
            throw new InvalidOperationException(configurationFault);
        if (!options.HasRequiredCredentials)
        {
            throw new InvalidOperationException(
                $"cTrader execution credentials and the {options.Mode} account ID must come from local configuration or environment variables.");
        }

        services.AddSingleton(options);
        services.AddSingleton(endpoint);
        services.AddSingleton<IAdapterEventScheduler, CTraderSerializedEventScheduler>();
        services.AddSingleton<CTraderExecutionAdapter>(provider =>
        {
            var transport = transportFactory?.Invoke(provider, endpoint) ??
                new CTraderTlsExecutionTransport(endpoint);
            try
            {
                return new CTraderExecutionAdapter(
                    provider.GetRequiredService<CTraderExecutionOptions>(),
                    transport,
                    provider.GetRequiredService<IClock>(),
                    provider.GetRequiredService<IAdapterEventScheduler>(),
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
            provider.GetRequiredService<CTraderExecutionAdapter>());
        return services;
    }
}
