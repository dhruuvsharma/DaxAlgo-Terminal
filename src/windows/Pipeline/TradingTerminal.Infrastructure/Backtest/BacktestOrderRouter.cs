using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingTerminal.Core.Risk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// <see cref="IOrderRouter"/> backed by a <see cref="SimulatedOrderBook"/>. Synchronous —
/// the returned tasks complete immediately. The book's <c>Events</c> stream is exposed
/// (after risk-rejection events are interleaved) as <see cref="OrderEvents"/>.
///
/// When an <see cref="IRiskManager"/> is supplied, <see cref="PlaceOrderAsync"/> runs every
/// request through <c>Evaluate</c> first; rejections are surfaced as a <c>Rejected</c>
/// <see cref="OrderEvent"/> on the same stream so strategies see them just like any other
/// terminal state. Fills are forwarded back into the risk manager to keep its position and
/// daily-PnL accounting in sync.
/// </summary>
public sealed class BacktestOrderRouter : IOrderRouter, IStrategySignalSink
{
    private readonly SimulatedOrderBook _book;
    private readonly IRiskManager? _risk;
    private readonly Subject<OrderEvent> _localEvents = new();
    private readonly Dictionary<string, string> _symbolByClientId = new(StringComparer.Ordinal);
    private readonly IClock? _clock;
    private readonly List<StrategySignalEvent> _signals = [];

    public BacktestOrderRouter(
        SimulatedOrderBook book,
        IRiskManager? risk = null,
        IClock? clock = null)
    {
        _book = book;
        _risk = risk;
        _clock = clock;
        if (_risk is not null)
        {
            _book.Events.Subscribe(evt =>
            {
                if (_symbolByClientId.TryGetValue(evt.ClientOrderId, out var symbol))
                    _risk.RecordFill(symbol, evt);
            });
        }
    }

    public IObservable<OrderEvent> OrderEvents => _book.Events.Merge(_localEvents);

    public IReadOnlyList<StrategySignalEvent> Signals => _signals;

    public Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateSignal(signal);
        _signals.Add(new StrategySignalEvent(_clock?.UtcNow ?? DateTime.UtcNow, signal));
        return Task.CompletedTask;
    }

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        if (_risk is not null)
        {
            var (allowed, reason) = _risk.Evaluate(request);
            if (!allowed)
            {
                var rejected = new OrderEvent(
                    TimestampUtc: DateTime.UtcNow,
                    ClientOrderId: request.ClientOrderId,
                    BrokerOrderId: null,
                    Side: request.Side,
                    State: OrderState.Rejected,
                    FilledQuantity: 0,
                    AverageFillPrice: null,
                    RejectReason: reason);
                _localEvents.OnNext(rejected);
                return Task.FromResult(new OrderResult(
                    request.ClientOrderId, null, OrderState.Rejected, reason));
            }
            _symbolByClientId[request.ClientOrderId] = request.Contract.Symbol;
        }
        return Task.FromResult(_book.Submit(request));
    }

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
    {
        _book.Cancel(clientOrderId);
        return Task.CompletedTask;
    }

    private static void ValidateSignal(StrategySignal signal)
    {
        if (!Enum.IsDefined(signal.Kind) || !double.IsFinite(signal.Strength) ||
            signal.Strength is < 0d or > 1d || signal.NoteId < 0)
            throw new ArgumentOutOfRangeException(nameof(signal), "Invalid strategy signal.");
    }
}
