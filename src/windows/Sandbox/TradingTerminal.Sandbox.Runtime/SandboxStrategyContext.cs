using DaxAlgo.Sdk;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>The capability-only context supplied to one Pro sandbox kernel instance.</summary>
public sealed class SandboxStrategyContext : IStrategyRuntimeContext, IDisposable
{
    private int _disposed;

    public SandboxStrategyContext(
        IMarketDataView data,
        IClock clock,
        IParameters parameters,
        IVirtualBook book,
        IAlertSink alerts)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        Book = book ?? throw new ArgumentNullException(nameof(book));
        Alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
    }

    public IMarketDataView Data { get; }

    public IClock Clock { get; }

    public IParameters Parameters { get; }

    public IVirtualBook Book { get; }

    public IAlertSink Alerts { get; }

    /// <summary>Disposes the owned market-data projection and all of its hub subscriptions.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Data is IDisposable disposable)
            disposable.Dispose();

        GC.SuppressFinalize(this);
    }
}
