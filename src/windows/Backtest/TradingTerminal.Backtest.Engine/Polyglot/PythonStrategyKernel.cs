using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Polyglot;

/// <summary>
/// Runs a Python-authored strategy (daxalgo_bt) as a long-lived subprocess and bridges it to the new
/// engine over stdin/stdout: it streams each quote (plus the current position) to Python and reads
/// back an order action, which it submits through the engine's router. All accounting, costs, fills,
/// and the report stay in C#; Python only decides what to do per quote. The per-quote round-trip is
/// inherently slower than a native kernel — that's the tradeoff for authoring in Python.
///
/// The process is opened in <see cref="OnStartAsync"/> and torn down in <see cref="OnEndAsync"/> /
/// <see cref="Dispose"/>.
/// </summary>
public sealed class PythonStrategyKernel : IStrategyKernel, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _pythonExe;
    private readonly string _scriptPath;

    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public PythonStrategyKernel(string pythonExe, string scriptPath)
    {
        _pythonExe = pythonExe;
        _scriptPath = scriptPath;
    }

    public async Task OnStartAsync(IStrategyContext ctx, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_scriptPath);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch {_pythonExe}.");
        _stdin = _proc.StandardInput;
        _stdout = _proc.StandardOutput;
        _ = Task.Run(async () => { try { await _proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false); } catch { } }, ct);

        var json = JsonSerializer.Serialize(ctx.Parameters.Values);
        await _stdin.WriteLineAsync($"START {json}".AsMemory(), ct).ConfigureAwait(false);
        await _stdin.FlushAsync(ct).ConfigureAwait(false);
        await _stdout.ReadLineAsync(ct).ConfigureAwait(false); // READY handshake
    }

    public async Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
    {
        if (_stdin is null || _stdout is null) return;

        var pos = ctx.Portfolio.PositionOf(instrument).Quantity;
        var ts = (quote.TimestampUtc - DateTime.UnixEpoch).TotalSeconds;
        var line = string.Create(Inv, $"Q {ts} {quote.Bid} {quote.Ask} {pos}");
        await _stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _stdin.FlushAsync(ct).ConfigureAwait(false);

        var resp = await _stdout.ReadLineAsync(ct).ConfigureAwait(false);
        await ApplyAsync(resp, instrument, pos, ctx, ct).ConfigureAwait(false);
    }

    public async Task OnEndAsync(IStrategyContext ctx, CancellationToken ct)
    {
        if (_stdin is not null)
        {
            try
            {
                await _stdin.WriteLineAsync("END".AsMemory(), ct).ConfigureAwait(false);
                await _stdin.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { /* process may already be gone */ }
        }
        Dispose();
    }

    private static async Task ApplyAsync(string? resp, InstrumentId instrument, long position, IStrategyContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resp) || resp == "NONE") return;
        if (ctx.Universe.Find(instrument)?.Contract is not { } contract) return;

        var parts = resp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToUpperInvariant();

        if (cmd == "FLAT")
        {
            if (position > 0) await Market(ctx, contract, OrderSide.Sell, position, ct).ConfigureAwait(false);
            else if (position < 0) await Market(ctx, contract, OrderSide.Buy, -position, ct).ConfigureAwait(false);
            return;
        }

        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.Integer, Inv, out var qty) || qty <= 0) return;
        if (cmd == "BUY") await Market(ctx, contract, OrderSide.Buy, qty, ct).ConfigureAwait(false);
        else if (cmd == "SELL") await Market(ctx, contract, OrderSide.Sell, qty, ct).ConfigureAwait(false);
    }

    private static Task Market(IStrategyContext ctx, Contract contract, OrderSide side, long qty, CancellationToken ct) =>
        ctx.Router.PlaceOrderAsync(new OrderRequest(Guid.NewGuid().ToString("N"), contract, side, OrderType.Market, qty), ct);

    public void Dispose()
    {
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_proc is { } p && !p.HasExited)
            {
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _proc?.Dispose();
        _proc = null;
        _stdin = null;
        _stdout = null;
    }
}
