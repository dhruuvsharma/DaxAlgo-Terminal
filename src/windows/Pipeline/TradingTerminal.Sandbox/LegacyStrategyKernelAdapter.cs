using System.Reflection;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Sandbox;

/// <summary>
/// Presents one already-constructed legacy <see cref="IBacktestStrategy"/> as a declarative
/// <see cref="DaxAlgo.Sdk.IStrategyKernel"/> for a single canonical instrument.
/// </summary>
/// <remarks>
/// This is interface compatibility only. It does not rewrite, isolate, or otherwise retrofit the
/// legacy strategy's IL; loading and execution remain subject to the host's existing Curated policy.
/// </remarks>
public sealed class LegacyStrategyKernelAdapter : DaxAlgo.Sdk.IStrategyKernel, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly IBacktestStrategy _legacyStrategy;
    private readonly InstrumentId _instrument;

    private PositionTrackingOrderRouter? _router;
    private bool _stoppingOrStopped;
    private int _legacyDisposed;

    /// <summary>
    /// Creates an adapter using the legacy loader convention: a public static
    /// <c>StrategyParameterSchema Schema</c>, or <see cref="StrategyParameterSchema.Empty"/>.
    /// </summary>
    public LegacyStrategyKernelAdapter(
        IBacktestStrategy legacyStrategy,
        InstrumentId instrument)
        : this(legacyStrategy, instrument, ResolveStaticSchema(legacyStrategy))
    {
    }

    /// <summary>Creates an adapter with the parameter schema already resolved by the loader.</summary>
    public LegacyStrategyKernelAdapter(
        IBacktestStrategy legacyStrategy,
        InstrumentId instrument,
        StrategyParameterSchema parameterSchema)
    {
        ArgumentNullException.ThrowIfNull(legacyStrategy);
        ArgumentNullException.ThrowIfNull(parameterSchema);
        if (instrument.IsNone)
            throw new ArgumentException("A bound instrument is required.", nameof(instrument));

        _legacyStrategy = legacyStrategy;
        _instrument = instrument;
        Schema = parameterSchema;
        DataRequirement = InferDataRequirement(legacyStrategy.GetType());
    }

    public StrategyParameterSchema Schema { get; }

    public StrategyDataRequirement DataRequirement { get; }

    public async Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Data.Instruments.Contains(_instrument))
        {
            var message =
                $"The sandbox context does not authorize the legacy adapter's bound instrument {_instrument}.";
            TryAlert(
                context.Alerts,
                message,
                AlertLevel.Error,
                "legacy-adapter-instrument-unauthorized");
            throw new InvalidOperationException(message);
        }

        PositionTrackingOrderRouter router;
        lock (_lifecycleGate)
        {
            if (_stoppingOrStopped)
                throw new InvalidOperationException("The legacy strategy adapter has already stopped.");
            if (_router is not null)
                throw new InvalidOperationException("The legacy strategy adapter has already started.");

            router = new PositionTrackingOrderRouter(
                context.Book,
                _instrument,
                context.Clock,
                _legacyStrategy,
                context.Alerts);
            _router = router;
        }

        try
        {
            await _legacyStrategy.OnStartAsync(context.Clock, router, ct);
        }
        catch
        {
            lock (_lifecycleGate)
            {
                _router = null;
                _stoppingOrStopped = true;
            }
            try
            {
                router.Dispose();
                await DisposeLegacyAsync();
            }
            catch (Exception cleanupException)
            {
                TryAlert(
                    context.Alerts,
                    $"Legacy strategy cleanup failed after start fault ({cleanupException.GetType().Name}).",
                    AlertLevel.Error,
                    "legacy-adapter-cleanup-fault");
            }
            throw;
        }
    }

    public async Task OnQuoteAsync(
        Quote quote,
        IStrategyRuntimeContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(context);
        if (!Accepts(quote.InstrumentId, context))
            return;

        var router = RequireRouter();
        router.TryUpdateReferencePrice(ReferencePrice(quote.Bid, quote.Ask));
        var tick = new Tick(
            quote.EventTimeUtc,
            quote.Bid,
            quote.Ask,
            quote.BidSize,
            quote.AskSize);
        await _legacyStrategy.OnTickAsync(tick, context.Clock, router, ct);
    }

    public async Task OnBarAsync(
        OhlcvBar bar,
        IStrategyRuntimeContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        if (!Accepts(bar.InstrumentId, context))
            return;

        var router = RequireRouter();
        router.TryUpdateReferencePrice(bar.Close);
        await _legacyStrategy.OnBarAsync(bar.ToBar(), context.Clock, router, ct);
    }

    public async Task OnTradeAsync(
        TradePrint trade,
        IStrategyRuntimeContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(context);
        if (!Accepts(trade.InstrumentId, context))
            return;

        var router = RequireRouter();
        router.TryUpdateReferencePrice(trade.Price);
        await _legacyStrategy.OnTradeAsync(trade, context.Clock, router, ct);
    }

    public async Task OnDepthAsync(
        InstrumentId instrument,
        DepthSnapshot depth,
        IStrategyRuntimeContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(context);
        if (!Accepts(instrument, context))
            return;

        var router = RequireRouter();
        router.TryUpdateReferencePrice(ReferencePrice(depth.BestBid, depth.BestAsk));
        await _legacyStrategy.OnDepthAsync(depth, context.Clock, router, ct);
    }

    public async Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        PositionTrackingOrderRouter? router;
        lock (_lifecycleGate)
        {
            if (_stoppingOrStopped)
                return;

            _stoppingOrStopped = true;
            router = _router;
        }

        try
        {
            if (router is not null)
                await _legacyStrategy.OnEndAsync(context.Clock, router, ct);
        }
        finally
        {
            try
            {
                router?.Dispose();
                await DisposeLegacyAsync();
            }
            finally
            {
                lock (_lifecycleGate)
                    _router = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        PositionTrackingOrderRouter? router;
        lock (_lifecycleGate)
        {
            _stoppingOrStopped = true;
            router = _router;
            _router = null;
        }

        router?.Dispose();
        await DisposeLegacyAsync();
        GC.SuppressFinalize(this);
    }

    private PositionTrackingOrderRouter RequireRouter()
    {
        lock (_lifecycleGate)
        {
            return _router
                ?? throw new InvalidOperationException(
                    "The legacy strategy adapter must be started before market events are forwarded.");
        }
    }

    private bool Accepts(InstrumentId instrument, IStrategyRuntimeContext context)
    {
        if (instrument == _instrument)
            return true;

        TryAlert(
            context.Alerts,
            $"Legacy strategy event for {instrument} was ignored; this adapter is bound to {_instrument}.",
            AlertLevel.Warning,
            "legacy-adapter-instrument-mismatch");
        return false;
    }

    private async ValueTask DisposeLegacyAsync()
    {
        if (Interlocked.Exchange(ref _legacyDisposed, 1) != 0)
            return;

        if (_legacyStrategy is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_legacyStrategy is IDisposable disposable)
            disposable.Dispose();
    }

    private static StrategyParameterSchema ResolveStaticSchema(IBacktestStrategy legacyStrategy)
    {
        ArgumentNullException.ThrowIfNull(legacyStrategy);

        var property = legacyStrategy.GetType().GetProperty(
            "Schema",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return property?.PropertyType == typeof(StrategyParameterSchema)
            ? property.GetValue(null) as StrategyParameterSchema ?? StrategyParameterSchema.Empty
            : StrategyParameterSchema.Empty;
    }

    private static StrategyDataRequirement InferDataRequirement(Type strategyType)
    {
        var requirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
        if (OverridesDefaultInterfaceMethod(strategyType, nameof(IBacktestStrategy.OnDepthAsync)))
            requirement |= StrategyDataRequirement.Depth;
        if (OverridesDefaultInterfaceMethod(strategyType, nameof(IBacktestStrategy.OnTradeAsync)))
            requirement |= StrategyDataRequirement.TradeTape;
        return requirement;
    }

    private static bool OverridesDefaultInterfaceMethod(Type strategyType, string methodName)
    {
        var interfaceType = typeof(IBacktestStrategy);
        var interfaceMethod = interfaceType.GetMethods().Single(method => method.Name == methodName);
        var map = strategyType.GetInterfaceMap(interfaceType);

        for (var index = 0; index < map.InterfaceMethods.Length; index++)
        {
            if (map.InterfaceMethods[index].MetadataToken != interfaceMethod.MetadataToken ||
                map.InterfaceMethods[index].Module != interfaceMethod.Module)
            {
                continue;
            }

            return map.TargetMethods[index].DeclaringType != interfaceType;
        }

        return false;
    }

    private static double ReferencePrice(double bid, double ask)
    {
        var validBid = double.IsFinite(bid) && bid > 0d;
        var validAsk = double.IsFinite(ask) && ask > 0d;
        return (validBid, validAsk) switch
        {
            (true, true) => bid + ((ask - bid) * 0.5d),
            (true, false) => bid,
            (false, true) => ask,
            _ => double.NaN,
        };
    }

    private static void TryAlert(
        IAlertSink alerts,
        string message,
        AlertLevel level,
        string dedupeKey)
    {
        try
        {
            alerts.Alert(message, level, dedupeKey);
        }
        catch
        {
            // Event rejection remains non-throwing even if the host alert route is unavailable.
        }
    }
}
