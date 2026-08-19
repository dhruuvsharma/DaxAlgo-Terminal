using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Sandbox;

/// <summary>The complete read/input capability context for a sandboxed visualizer.</summary>
public sealed class SandboxVisualizerContext : IVisualizerContext, IDisposable
{
    private readonly IDisposable? _ownedResource;
    private int _disposed;

    public SandboxVisualizerContext(
        IMarketDataView data,
        IClock clock,
        IParameters parameters,
        IAlertSink alerts,
        IRenderSurface? surface = null)
        : this(data, clock, parameters, alerts, ownedResource: null, surface)
    {
    }

    internal SandboxVisualizerContext(
        IMarketDataView data,
        IClock clock,
        IParameters parameters,
        IAlertSink alerts,
        IDisposable? ownedResource,
        IRenderSurface? surface = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(alerts);

        Data = data;
        Clock = clock;
        Parameters = parameters;
        Alerts = alerts;
        // Null-object rather than null: a visualizer must never have to null-check where it draws,
        // and a host with nothing on screen is a legitimate state rather than a missing dependency.
        Surface = surface ?? NullRenderSurface.Instance;
        _ownedResource = ownedResource;
    }

    public IMarketDataView Data { get; }

    public IClock Clock { get; }

    public IParameters Parameters { get; }

    public IAlertSink Alerts { get; }

    /// <summary>Where this visualizer draws; a discarding surface when the host renders nothing.</summary>
    public IRenderSurface Surface { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ownedResource?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Builds a complete visualizer context from host-owned inputs and fixed output routes.</summary>
public static class SandboxVisualizerContextFactory
{
    public static SandboxVisualizerContext Create(
        IReadOnlySet<InstrumentId> instruments,
        StrategyDataRequirement dataRequirement,
        IMarketDataHub hub,
        IClock clock,
        StrategyParameterSchema parameterSchema,
        IReadOnlyDictionary<string, object?>? currentValues,
        string source,
        Action<string, string, string> appendActivityLog,
        Action<AlertRecord> showBanner,
        int retentionBound = ScopedMarketDataView.DefaultRetentionBound,
        TimeSpan? alertWindow = null,
        int maxAlertsPerWindow = MediatedAlertSink.DefaultMaxAlertsPerWindow)
    {
        var parameters = new SandboxParameters(parameterSchema, currentValues);
        var alerts = new MediatedAlertSink(
            source,
            clock,
            appendActivityLog,
            showBanner,
            alertWindow,
            maxAlertsPerWindow);
        var data = new ScopedMarketDataView(instruments, dataRequirement, hub, retentionBound);

        return new SandboxVisualizerContext(data, clock, parameters, alerts, ownedResource: data);
    }

    // The Pro runtime bridge composes these same four capabilities and injects its host-owned
    // IVirtualBook there. This open-core read-side project intentionally does not implement
    // IStrategyRuntimeContext or reference any account/execution engine.
}
