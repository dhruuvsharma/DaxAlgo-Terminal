#if HAS_NTAPI
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.NinjaTrader;

/// <summary>
/// Real NinjaTrader 8 client. Compiled only when NTDirect.dll is resolvable
/// (lib/NTDirect.dll, $(NinjaTraderApiDll), or %USERPROFILE%\Documents\NinjaTrader 8\bin64\NTDirect.dll).
///
/// NinjaTrader hosts itself: NT must already be running and signed in. This client
/// communicates with it through the NTDirect ANSI C ABI via P/Invoke. NTDirect does
/// not push callbacks — we poll bid/ask after <c>SubscribeMarketData</c> at ~200 ms
/// so the rest of the app sees a tick stream comparable to IB's <c>reqMktData</c>.
///
/// NTDirect does not expose historical bars; <see cref="RequestHistoricalBarsAsync"/>
/// logs a warning and returns synthetic bars so charts still render. To get real
/// historical from NT we'd need a NinjaScript add-on bridge (out of scope here).
/// </summary>
public sealed class RealNinjaClient : IBrokerClient
{
    private readonly ILogger<RealNinjaClient> _logger;
    private readonly IOptions<NinjaTraderOptions> _options;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly Random _historicalFiller = new(31);
    private readonly object _gate = new();
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _connectionCts;

    public RealNinjaClient(ILogger<RealNinjaClient> logger, IOptions<NinjaTraderOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public BrokerKind Kind => BrokerKind.NinjaTrader;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            // NTDirect: Connected(0) returns 0 when NinjaTrader is running and the bridge is up.
            var rc = NTDirect.Connected(0);
            if (rc != 0)
            {
                _logger.LogWarning("NinjaTrader is not running or NTDirect bridge is unavailable (rc={Rc}). " +
                                   "Start NinjaTrader 8 and ensure the AT Interface is enabled.", rc);
                _state.OnNext(Core.Domain.ConnectionState.Failed);
                return Task.CompletedTask;
            }

            _connectionCts = new CancellationTokenSource();
            _logger.LogInformation("Connected to NinjaTrader (account={Account})", _options.Value.AccountName);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "NTDirect.dll not found at runtime");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NinjaTrader connect failed");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
        return Task.CompletedTask;
    }

    // NTDirect exposes no instrument-list call, so we surface a curated futures catalog
    // (what a default Sim101 account trades). Users edit CuratedInstrumentCatalog to extend.
    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
        Task.FromResult(CuratedInstrumentCatalog.Futures);

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connectionCts?.Cancel();
        lock (_gate)
        {
            foreach (var instrument in _subscribed)
            {
                try { NTDirect.UnsubscribeMarketData(instrument); }
                catch (Exception ex) { _logger.LogWarning(ex, "NTDirect unsubscribe failed for {Sym}", instrument); }
            }
            _subscribed.Clear();
        }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        // NTDirect doesn't expose historical bars. Synthesize a plausible series anchored on the
        // current LastPrice so the chart has a reasonable baseline before live ticks arrive.
        _logger.LogWarning(
            "NinjaTrader historical bars unavailable through NTDirect — synthesizing {Symbol} bars. " +
            "Install a NinjaScript bridge for real history.", contract.Symbol);

        var instrument = ToNtInstrument(contract);
        var seed = SafeLastPrice(instrument);
        if (seed <= 0) seed = 100;

        var step = barSize.ToTimeSpan();
        var count = Math.Max(1, (int)(duration.TotalSeconds / step.TotalSeconds));
        var startUtc = DateTime.UtcNow - TimeSpan.FromTicks(step.Ticks * count);
        var bars = new List<Bar>(count);
        var price = seed;
        for (int i = 0; i < count; i++)
        {
            var ts = startUtc + TimeSpan.FromTicks(step.Ticks * i);
            var open = price;
            var drift = (_historicalFiller.NextDouble() - 0.5) * (seed * 0.002);
            var close = Math.Max(0.01, open + drift);
            var high = Math.Max(open, close) + _historicalFiller.NextDouble() * (seed * 0.001);
            var low = Math.Max(0.01, Math.Min(open, close) - _historicalFiller.NextDouble() * (seed * 0.001));
            var vol = (long)(10_000 + _historicalFiller.NextDouble() * 50_000);
            bars.Add(new Bar(ts, open, high, low, close, vol));
            price = close;
        }
        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_state.Value is not Core.Domain.ConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        // Build bars by aggregating polled last-price ticks at the requested cadence.
        // Not as accurate as a true historical-update feed but keeps the strategy host happy.
        var instrument = ToNtInstrument(contract);
        EnsureSubscribed(instrument);

        var step = barSize.ToTimeSpan();
        var ch = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var open = SafeLastPrice(instrument);
                    var high = open;
                    var low = open;
                    var close = open;
                    long volStart = SafeVolume(instrument);
                    var deadline = DateTime.UtcNow + step;

                    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(200, ct);
                        var p = SafeLastPrice(instrument);
                        if (p <= 0) continue;
                        if (p > high) high = p;
                        if (p < low || low == 0) low = p;
                        close = p;
                    }

                    long volEnd = SafeVolume(instrument);
                    long vol = Math.Max(0, volEnd - volStart);

                    if (open > 0)
                        ch.Writer.TryWrite(new Bar(DateTime.UtcNow, open, high, low, close, vol));
                }
            }
            catch (OperationCanceledException) { }
            finally { ch.Writer.TryComplete(); }
        }, ct);

        await foreach (var bar in ch.Reader.ReadAllAsync(ct))
            yield return bar;
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_state.Value is not Core.Domain.ConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        var instrument = ToNtInstrument(contract);
        EnsureSubscribed(instrument);

        var ch = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                double prevBid = 0, prevAsk = 0;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(200, ct);
                    var bid = SafeBid(instrument);
                    var ask = SafeAsk(instrument);
                    if (bid <= 0 || ask <= 0) continue;
                    if (Math.Abs(bid - prevBid) < double.Epsilon &&
                        Math.Abs(ask - prevAsk) < double.Epsilon) continue;

                    prevBid = bid;
                    prevAsk = ask;
                    // NTDirect doesn't expose L1 sizes through these calls; report zero.
                    ch.Writer.TryWrite(new Tick(DateTime.UtcNow, bid, ask, 0, 0));
                }
            }
            catch (OperationCanceledException) { }
            finally { ch.Writer.TryComplete(); }
        }, ct);

        await foreach (var tick in ch.Reader.ReadAllAsync(ct))
            yield return tick;
    }

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        // NinjaTrader's NTDirect surface doesn't expose L2 — depth in NT8 requires the
        // NinjaScript SuperDOM / OrderFlow component, not the AT Interface. Use cTrader
        // for L2 from this terminal, or pair this broker selector with a dedicated NT8
        // NinjaScript bridge add-on (out of scope today).
        throw new NotSupportedException(
            "NinjaTrader's NTDirect API does not expose L2 depth. Use cTrader for L2 from this terminal.");

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, CancellationToken ct = default) =>
        // NTDirect's MarketData feed delivers "Last" prices but doesn't differentiate aggressor
        // side natively; a real implementation would subscribe via SubscribeMarketData with the
        // Last update kind and let the ingest layer infer aggressor via Lee-Ready. Not wired yet.
        throw new NotSupportedException(
            "NinjaTrader trade-tape ingest is not wired in this build.");

    private void EnsureSubscribed(string instrument)
    {
        lock (_gate)
        {
            if (!_subscribed.Add(instrument)) return;
        }
        try { NTDirect.SubscribeMarketData(instrument); }
        catch (Exception ex) { _logger.LogWarning(ex, "NTDirect subscribe failed for {Sym}", instrument); }
    }

    private string ToNtInstrument(Contract contract)
    {
        // Stocks: bare symbol. Futures: append the configured contract month if the caller didn't
        // already provide one (NinjaTrader expects e.g. "ES 06-26").
        var sym = contract.Symbol;
        var month = _options.Value.DefaultFuturesContractMonth;
        if (!string.IsNullOrWhiteSpace(month) &&
            (contract.SecType == "FUT" || contract.SecType == "CONTFUT") &&
            !sym.Contains(' '))
        {
            return $"{sym} {month}";
        }
        return sym;
    }

    private double SafeBid(string instrument)
    {
        try { return NTDirect.Bid(instrument); } catch { return 0; }
    }

    private double SafeAsk(string instrument)
    {
        try { return NTDirect.Ask(instrument); } catch { return 0; }
    }

    private double SafeLastPrice(string instrument)
    {
        try { return NTDirect.LastPrice(instrument); } catch { return 0; }
    }

    private long SafeVolume(string instrument)
    {
        try { return NTDirect.Volume(instrument); } catch { return 0; }
    }

    public ValueTask DisposeAsync()
    {
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// P/Invoke wrappers around the NTDirect.dll exports shipped with NinjaTrader 8.
/// All exports use the ANSI C ABI; the runtime resolves the DLL through Windows'
/// search path (the build target copies the resolved file alongside the assembly).
/// </summary>
internal static class NTDirect
{
    private const string Lib = "NTDirect.dll";

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "Connected")]
    public static extern int Connected(int showMessage);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "SubscribeMarketData")]
    public static extern int SubscribeMarketData([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "UnsubscribeMarketData")]
    public static extern int UnsubscribeMarketData([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "Bid")]
    public static extern double Bid([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "Ask")]
    public static extern double Ask([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "LastPrice")]
    public static extern double LastPrice([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "Volume")]
    public static extern long Volume([MarshalAs(UnmanagedType.LPStr)] string instrument);

    [DllImport(Lib, CharSet = CharSet.Ansi, EntryPoint = "Command")]
    public static extern int Command(
        [MarshalAs(UnmanagedType.LPStr)] string command,
        [MarshalAs(UnmanagedType.LPStr)] string account,
        [MarshalAs(UnmanagedType.LPStr)] string instrument,
        [MarshalAs(UnmanagedType.LPStr)] string action,
        int quantity,
        [MarshalAs(UnmanagedType.LPStr)] string orderType,
        double limitPrice,
        double stopPrice,
        [MarshalAs(UnmanagedType.LPStr)] string tif,
        [MarshalAs(UnmanagedType.LPStr)] string oco,
        [MarshalAs(UnmanagedType.LPStr)] string orderId,
        [MarshalAs(UnmanagedType.LPStr)] string template,
        [MarshalAs(UnmanagedType.LPStr)] string strategy);
}
#endif
