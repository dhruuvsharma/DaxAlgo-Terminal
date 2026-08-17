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
/// <see cref="Universe"/> (so an order targets the right book/position in a portfolio run), then
/// pushes it into the <see cref="SimulatedOrderBook"/>. Re-publishes order events on
/// <see cref="OrderEvents"/> for any external subscriber; the engine itself listens to the book
/// directly so it also gets the instrument tag.
/// </summary>
internal sealed class EngineOrderRouter : IOrderRouter, IStrategySignalSink
{
    private readonly SimulatedOrderBook _book;
    private readonly Universe _universe;
    private readonly IClock _clock;
    private readonly Subject<OrderEvent> _events = new();
    private readonly List<StrategySignalEvent> _signals = [];

    public EngineOrderRouter(SimulatedOrderBook book, Universe universe, IClock clock)
    {
        _book = book;
        _universe = universe;
        _clock = clock;
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

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(_book.Submit(request, Resolve(request.Contract)));

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _book.Cancel(clientOrderId);
        return Task.CompletedTask;
    }

    private InstrumentId Resolve(Contract contract)
    {
        foreach (var spec in _universe.Instruments)
            if (string.Equals(spec.Contract.Symbol, contract.Symbol, StringComparison.OrdinalIgnoreCase))
                return spec.Id;
        return _universe.Primary.Id; // single-instrument runs (and unmatched symbols) target the primary
    }
}
