#if HAS_IBAPI
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
// Note: do NOT `using IBApi;` here — it collides with TradingTerminal.Core.Domain on Bar/Contract.
// Reference IB types via the IBApi.Xxx prefix throughout this file.

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Real Interactive Brokers client. Compiled only when the TWS CSharpAPI.dll is resolvable
/// (lib/CSharpAPI.dll, $(TwsApiClientDll), or C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\).
/// Wraps <see cref="IBApi.EClientSocket"/> behind <see cref="IIbClient"/>; callbacks come off
/// the IB reader thread into per-request channels.
/// </summary>
/// <remarks>
/// Inherits <see cref="IBApi.DefaultEWrapper"/> so we only override the methods we actually use —
/// the API has 170+ EWrapper callbacks and most aren't relevant to v1 (charts only, no orders).
/// </remarks>
public sealed class RealIbClient : IBApi.DefaultEWrapper, IIbClient
{
    private readonly ILogger<RealIbClient> _logger;
    private readonly IBApi.EReaderSignal _signal = new IBApi.EReaderMonitorSignal();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    private IBApi.EClientSocket? _client;
    private IBApi.EReader? _reader;
    private Thread? _readerThread;
    private int _nextRequestId;
    private TaskCompletionSource? _connectTcs;

    private readonly Dictionary<int, HistoricalRequest> _historical = new();
    private readonly Dictionary<int, Channel<Bar>> _streams = new();
    private readonly object _gate = new();

    public RealIbClient(ILogger<RealIbClient> logger)
    {
        _logger = logger;
    }

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(string host, int port, int clientId, CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        _client = new IBApi.EClientSocket(this, _signal);
        _connectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _client.eConnect(host, port, clientId);

        _reader = new IBApi.EReader(_client, _signal);
        _reader.Start();
        _readerThread = new Thread(() =>
        {
            while (_client?.IsConnected() == true)
            {
                _signal.waitForSignal();
                try { _reader.processMsgs(); }
                catch (Exception ex) { _logger.LogError(ex, "IB reader loop error"); }
            }
        })
        { IsBackground = true, Name = "IB-Reader" };
        _readerThread.Start();

        // nextValidId callback completes _connectTcs (= API handshake done).
        using var reg = ct.Register(() => _connectTcs?.TrySetCanceled());
        await _connectTcs.Task.ConfigureAwait(false);
        _state.OnNext(Core.Domain.ConnectionState.Connected);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try { _client?.eDisconnect(); }
        catch (Exception ex) { _logger.LogWarning(ex, "IB disconnect error"); }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        if (_client?.IsConnected() != true) throw new InvalidOperationException("Not connected.");
        var reqId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<IReadOnlyList<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new HistoricalRequest(tcs);
        lock (_gate) _historical[reqId] = req;

        var ibContract = ToIbContract(contract);
        _client.reqHistoricalData(reqId, ibContract,
            endDateTime: string.Empty,
            durationStr: ToIbDuration(duration),
            barSizeSetting: barSize.ToIbString(),
            whatToShow: "TRADES",
            useRTH: 1,
            formatDate: 1,
            keepUpToDate: false,
            chartOptions: null);

        ct.Register(() =>
        {
            try { _client.cancelHistoricalData(reqId); } catch { /* swallow */ }
            tcs.TrySetCanceled();
            lock (_gate) _historical.Remove(reqId);
        });

        return tcs.Task;
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client?.IsConnected() != true) throw new InvalidOperationException("Not connected.");

        var reqId = Interlocked.Increment(ref _nextRequestId);
        var ch = Channel.CreateUnbounded<Bar>();
        lock (_gate) _streams[reqId] = ch;

        var ibContract = ToIbContract(contract);
        _client.reqHistoricalData(reqId, ibContract,
            endDateTime: string.Empty,
            durationStr: ToIbDuration(TimeSpan.FromHours(1)),
            barSizeSetting: barSize.ToIbString(),
            whatToShow: "TRADES",
            useRTH: 1,
            formatDate: 1,
            keepUpToDate: true,
            chartOptions: null);

        await using var _ = ct.Register(() =>
        {
            try { _client.cancelHistoricalData(reqId); } catch { /* swallow */ }
            ch.Writer.TryComplete();
            lock (_gate) _streams.Remove(reqId);
        });

        await foreach (var bar in ch.Reader.ReadAllAsync(ct))
            yield return bar;
    }

    private static IBApi.Contract ToIbContract(Core.Domain.Contract c) => new()
    {
        Symbol = c.Symbol,
        SecType = c.SecType,
        Exchange = c.Exchange,
        Currency = c.Currency,
        PrimaryExch = c.PrimaryExchange
    };

    private static string ToIbDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1) return $"{(int)Math.Ceiling(duration.TotalDays)} D";
        if (duration.TotalHours >= 1) return $"{(int)Math.Ceiling(duration.TotalHours * 3600)} S";
        return $"{(int)Math.Max(60, Math.Ceiling(duration.TotalSeconds))} S";
    }

    private static Bar ParseBar(IBApi.Bar b)
    {
        DateTime tsUtc;
        if (long.TryParse(b.Time, out var epoch))
        {
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }
        else
        {
            // Either "yyyyMMdd  HH:mm:ss" or "yyyyMMdd". TWS returns its login-timezone clock,
            // we treat it as local then convert. For higher precision, parse the trailing TZ token.
            var s = b.Time.Trim();
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dt = DateTime.ParseExact(
                parts.Length >= 2 ? parts[0] + " " + parts[1] : parts[0],
                parts.Length >= 2 ? "yyyyMMdd HH:mm:ss" : "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            tsUtc = dt.ToUniversalTime();
        }
        return new Bar(tsUtc, b.Open, b.High, b.Low, b.Close, (long)b.Volume);
    }

    public ValueTask DisposeAsync()
    {
        try { _client?.eDisconnect(); } catch { }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        _state.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class HistoricalRequest
    {
        public HistoricalRequest(TaskCompletionSource<IReadOnlyList<Bar>> tcs) { Tcs = tcs; }
        public TaskCompletionSource<IReadOnlyList<Bar>> Tcs { get; }
        public List<Bar> Bars { get; } = new();
    }

    // ---------- EWrapper overrides (only what we use; everything else is no-op via DefaultEWrapper). ----------

    public override void nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _nextRequestId, Math.Max(_nextRequestId, orderId));
        _connectTcs?.TrySetResult();
    }

    public override void historicalData(int reqId, IBApi.Bar bar)
    {
        HistoricalRequest? req;
        Channel<Bar>? stream;
        lock (_gate)
        {
            _historical.TryGetValue(reqId, out req);
            _streams.TryGetValue(reqId, out stream);
        }
        var parsed = ParseBar(bar);
        req?.Bars.Add(parsed);
        stream?.Writer.TryWrite(parsed);
    }

    public override void historicalDataEnd(int reqId, string startDateStr, string endDateStr)
    {
        HistoricalRequest? req;
        lock (_gate) { _historical.TryGetValue(reqId, out req); _historical.Remove(reqId); }
        req?.Tcs.TrySetResult(req.Bars);
    }

    public override void historicalDataUpdate(int reqId, IBApi.Bar bar)
    {
        Channel<Bar>? stream;
        lock (_gate) _streams.TryGetValue(reqId, out stream);
        stream?.Writer.TryWrite(ParseBar(bar));
    }

    public override void error(Exception e) =>
        _logger.LogError(e, "IB error");

    public override void error(string str) =>
        _logger.LogWarning("IB error: {Message}", str);

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        // 2104/2106/2158 are connectivity status notices, not real errors.
        if (errorCode is 2104 or 2106 or 2158)
            _logger.LogInformation("IB status: {Code} {Msg}", errorCode, errorMsg);
        else
            _logger.LogWarning("IB error req={Id} code={Code}: {Msg}", id, errorCode, errorMsg);

        if (id >= 0)
        {
            HistoricalRequest? req;
            Channel<Bar>? stream;
            lock (_gate)
            {
                _historical.TryGetValue(id, out req);
                _streams.TryGetValue(id, out stream);
            }
            req?.Tcs.TrySetException(new InvalidOperationException($"IB {errorCode}: {errorMsg}"));
            stream?.Writer.TryComplete(new InvalidOperationException($"IB {errorCode}: {errorMsg}"));
        }
    }

    public override void connectionClosed()
    {
        _logger.LogWarning("IB connection closed");
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }
}
#endif
