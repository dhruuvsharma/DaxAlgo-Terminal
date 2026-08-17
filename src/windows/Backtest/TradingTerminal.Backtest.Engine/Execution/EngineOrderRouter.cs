using System.Reactive.Subjects;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// The kernel-facing order seam for the backtester. Resolves each <see cref="OrderRequest"/>'s
/// <see cref="Contract"/> to a canonical <see cref="InstrumentId"/> against the run's
/// <see cref="Universe"/>, optionally delays submission by <see cref="ExecutionSpec.LatencyMs"/>,
/// then pushes into the <see cref="SimulatedOrderBook"/>.
/// </summary>
internal sealed class EngineOrderRouter : IOrderRouter, IStrategySignalSink
{
    private readonly SimulatedOrderBook _book;
    private readonly Universe _universe;
    private readonly IClock _clock;
    private readonly double _latencyMs;
    private readonly Subject<OrderEvent> _events = new();
    private readonly List<StrategySignalEvent> _signals = [];
    private readonly List<PendingSubmit> _pending = [];
    private long _nextDeferredId;

    public EngineOrderRouter(SimulatedOrderBook book, Universe universe, IClock clock, double latencyMs = 0)
    {
        _book = book;
        _universe = universe;
        _clock = clock;
        _latencyMs = latencyMs < 0 ? 0 : latencyMs;
        _book.Event += (_, evt) => _events.OnNext(evt);
    }

    public IObservable<OrderEvent> OrderEvents => _events;

    public IReadOnlyList<StrategySignalEvent> Signals => _signals;

    public Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(signal.Kind) || !double.IsFinite(signal.Strength) ||
            signal.Strength is < 0d or > 1d || signal.NoteId < 0)
            throw new ArgumentOutOfRangeException(nameof(signal), "Invalid strategy signal.");
        _signals.Add(new StrategySignalEvent(_clock.UtcNow, signal));
        return Task.CompletedTask;
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var instrument = Resolve(request.Contract);

        if (_latencyMs <= 0)
            return Task.FromResult(_book.Submit(request, instrument));

        // Delay admission to the book — order is "Working" from the strategy's POV immediately.
        var deferredBrokerId = $"BT-LAT-{Interlocked.Increment(ref _nextDeferredId)}";
        var readyUtc = _clock.UtcNow.AddMilliseconds(_latencyMs);
        _pending.Add(new PendingSubmit(readyUtc, request, instrument, deferredBrokerId));

        return Task.FromResult(new OrderResult(request.ClientOrderId, deferredBrokerId, OrderState.Working));
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _pending.RemoveAll(p => string.Equals(p.Request.ClientOrderId, clientOrderId, StringComparison.Ordinal));
        _book.Cancel(clientOrderId);
        return Task.CompletedTask;
    }

    /// <summary>Admit latency-deferred orders whose ready time has passed. Call after each clock advance.</summary>
    public void ReleaseDue()
    {
        if (_pending.Count == 0) return;
        var now = _clock.UtcNow;
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.ReadyUtc > now) continue;
            _pending.RemoveAt(i);
            _book.Submit(p.Request, p.Instrument);
        }
    }

    private InstrumentId Resolve(Contract contract)
    {
        foreach (var spec in _universe.Instruments)
            if (string.Equals(spec.Contract.Symbol, contract.Symbol, StringComparison.OrdinalIgnoreCase))
                return spec.Id;
        return _universe.Primary.Id;
    }

    private sealed record PendingSubmit(
        DateTime ReadyUtc, OrderRequest Request, InstrumentId Instrument, string DeferredBrokerId);
}
