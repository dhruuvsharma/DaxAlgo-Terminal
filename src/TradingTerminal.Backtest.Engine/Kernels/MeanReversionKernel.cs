using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Kernels;

/// <summary>
/// A reference native kernel: rolling-window z-score mean reversion. Reads its parameters from the
/// run (so it sweeps and serializes like any other), keeps per-instrument state, and trades each
/// instrument independently — exercising quotes → signal → market order → fill → position the new
/// engine needs to validate. Parameters: <c>lookback</c>, <c>entryZ</c>, <c>exitZ</c>, <c>qty</c>.
/// </summary>
public sealed class MeanReversionKernel : IStrategyKernel
{
    private readonly Dictionary<InstrumentId, Queue<double>> _windows = new();
    private int _lookback;
    private double _entryZ;
    private double _exitZ;
    private long _qty;

    public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        _lookback = Math.Max(2, ctx.Parameters.GetInt("lookback", 50));
        _entryZ = ctx.Parameters.GetOr("entryZ", 2.0);
        _exitZ = ctx.Parameters.GetOr("exitZ", 0.5);
        _qty = Math.Max(1, ctx.Parameters.GetInt("qty", 1));
        return Task.CompletedTask;
    }

    public async Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
    {
        var mid = (quote.Bid + quote.Ask) * 0.5;
        if (!_windows.TryGetValue(instrument, out var window))
            _windows[instrument] = window = new Queue<double>(_lookback);

        window.Enqueue(mid);
        while (window.Count > _lookback) window.Dequeue();
        if (window.Count < _lookback) return;

        var mean = window.Average();
        var std = Math.Sqrt(window.Select(x => (x - mean) * (x - mean)).Sum() / window.Count);
        if (std <= 0) return;
        var z = (mid - mean) / std;

        var contract = ctx.Universe.Find(instrument)?.Contract;
        if (contract is null) return;
        var position = ctx.Portfolio.PositionOf(instrument);

        if (position.IsFlat)
        {
            if (z <= -_entryZ) await Market(ctx, contract, OrderSide.Buy, _qty, ct).ConfigureAwait(false);
            else if (z >= _entryZ) await Market(ctx, contract, OrderSide.Sell, _qty, ct).ConfigureAwait(false);
        }
        else if (position.IsLong && z >= -_exitZ)
        {
            await Market(ctx, contract, OrderSide.Sell, position.Quantity, ct).ConfigureAwait(false);
        }
        else if (position.IsShort && z <= _exitZ)
        {
            await Market(ctx, contract, OrderSide.Buy, Math.Abs(position.Quantity), ct).ConfigureAwait(false);
        }
    }

    private static Task Market(IStrategyContext ctx, Contract contract, OrderSide side, long qty, CancellationToken ct) =>
        ctx.Router.PlaceOrderAsync(
            new OrderRequest(Guid.NewGuid().ToString("N"), contract, side, OrderType.Market, qty), ct);
}
