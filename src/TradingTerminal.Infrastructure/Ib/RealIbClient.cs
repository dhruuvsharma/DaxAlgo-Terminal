#if HAS_IBAPI
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
// Note: do NOT `using IBApi;` here — it collides with TradingTerminal.Core.Domain on Bar/Contract.
// Reference IB types via the IBApi.Xxx prefix throughout this file.

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Real Interactive Brokers client. Compiled only when the TWS CSharpAPI.dll is resolvable
/// (lib/CSharpAPI.dll, $(TwsApiClientDll), or C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\).
/// Wraps <see cref="IBApi.EClientSocket"/> behind <see cref="IBrokerClient"/>; callbacks come off
/// the IB reader thread into per-request channels.
/// </summary>
/// <remarks>
/// Inherits <see cref="IBApi.DefaultEWrapper"/> so we only override the methods we actually use —
/// the API has 170+ EWrapper callbacks and most aren't relevant to v1 (charts only, no orders).
/// </remarks>
public sealed class RealIbClient : IBApi.DefaultEWrapper, IBrokerClient
{
    private readonly ILogger<RealIbClient> _logger;
    private readonly IOptions<InteractiveBrokersOptions> _options;
    private readonly IBApi.EReaderSignal _signal = new IBApi.EReaderMonitorSignal();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    private IBApi.EClientSocket? _client;
    private IBApi.EReader? _reader;
    private Thread? _readerThread;
    private int _nextRequestId;
    private TaskCompletionSource? _connectTcs;

    private readonly Dictionary<int, HistoricalRequest> _historical = new();
    private readonly Dictionary<int, Channel<Bar>> _streams = new();
    private readonly Dictionary<int, TickStream> _tickStreams = new();
    private readonly object _gate = new();

    public RealIbClient(ILogger<RealIbClient> logger, IOptions<InteractiveBrokersOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public BrokerKind Kind => BrokerKind.InteractiveBrokers;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var opt = _options.Value;
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        _client = new IBApi.EClientSocket(this, _signal);
        _connectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _client.eConnect(opt.Host, opt.Port, opt.ClientId);

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

        // Apply session-level market-data type. Calling with 1 (Live) is harmless but explicit.
        // 3/4 (Delayed / Delayed-Frozen) lets accounts without the underlying market-data
        // subscription still receive quotes, at ~15 minute lag and lower update cadence.
        var mdType = _options.Value.MarketDataType;
        if (mdType is >= 1 and <= 4)
        {
            try
            {
                _client.reqMarketDataType(mdType);
                _logger.LogInformation("IB market-data type set to {Type} ({Name})",
                    mdType, MarketDataTypeName(mdType));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set IB market-data type to {Type}", mdType);
            }
        }

        _state.OnNext(Core.Domain.ConnectionState.Connected);
    }

    private static string MarketDataTypeName(int t) => t switch
    {
        1 => "Live", 2 => "Frozen", 3 => "Delayed", 4 => "Delayed-Frozen", _ => "Unknown"
    };

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

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client?.IsConnected() != true) throw new InvalidOperationException("Not connected.");

        var reqId = Interlocked.Increment(ref _nextRequestId);
        var stream = new TickStream();
        lock (_gate) _tickStreams[reqId] = stream;

        var ibContract = ToIbContract(contract);
        // L1 streaming quotes — universally available where the user has any real-time market-data
        // subscription. We synthesise Tick records from tickPrice/tickSize callbacks. This is the
        // path that survives accounts without the (separately-priced) tick-by-tick subscription.
        _client.reqMktData(reqId, ibContract,
            genericTickList: string.Empty,
            snapshot: false,
            regulatorySnapshot: false,
            mktDataOptions: null);

        await using var _ = ct.Register(() =>
        {
            try { _client.cancelMktData(reqId); } catch { /* swallow */ }
            stream.Channel.Writer.TryComplete();
            lock (_gate) _tickStreams.Remove(reqId);
        });

        await foreach (var tick in stream.Channel.Reader.ReadAllAsync(ct))
            yield return tick;
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

    /// <summary>
    /// Per-request quote accumulator. <c>reqMktData</c> delivers bid and ask updates as
    /// independent callbacks; we keep the latest of each here so each emitted <see cref="Tick"/>
    /// carries both sides.
    /// </summary>
    private sealed class TickStream
    {
        public Channel<Tick> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<Tick>();
        public double Bid;
        public double Ask;
        public long BidSize;
        public long AskSize;
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

    // ---------- L1 quote streaming via reqMktData. We synthesise Ticks on bid/ask price changes. ----------
    // Field IDs (IBApi TickType): 1 = BID, 2 = ASK, 0 = BID_SIZE, 3 = ASK_SIZE.
    // When reqMarketDataType(3 or 4) is in effect, IB emits *delayed* equivalents:
    //   66 = DELAYED_BID, 67 = DELAYED_ASK, 75 = DELAYED_BID_SIZE, 76 = DELAYED_ASK_SIZE.
    // We accept both so the strategy still works when the user has no live subscription.

    public override void tickPrice(int reqId, int field, double price, IBApi.TickAttrib attribs)
    {
        TickStream? stream;
        lock (_gate) _tickStreams.TryGetValue(reqId, out stream);
        if (stream is null) return;

        bool isBid = field is 1 or 66;
        bool isAsk = field is 2 or 67;
        if (!isBid && !isAsk) return;
        if (price <= 0) return;

        bool changed;
        if (isBid)
        {
            changed = Math.Abs(price - stream.Bid) > double.Epsilon;
            if (changed) stream.Bid = price;
        }
        else
        {
            changed = Math.Abs(price - stream.Ask) > double.Epsilon;
            if (changed) stream.Ask = price;
        }

        if (!changed) return;
        if (stream.Bid <= 0 || stream.Ask <= 0) return; // wait until we have both sides

        stream.Channel.Writer.TryWrite(
            new Tick(DateTime.UtcNow, stream.Bid, stream.Ask, stream.BidSize, stream.AskSize));
    }

    public override void tickSize(int reqId, int field, decimal size)
    {
        TickStream? stream;
        lock (_gate) _tickStreams.TryGetValue(reqId, out stream);
        if (stream is null) return;

        switch (field)
        {
            case 0:  case 75: stream.BidSize = (long)size; break; // BID_SIZE / DELAYED_BID_SIZE
            case 3:  case 76: stream.AskSize = (long)size; break; // ASK_SIZE / DELAYED_ASK_SIZE
        }
        // No emit — only price changes drive the bid-tick rule. Sizes ride along on next price update.
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
            TickStream? tickStream;
            lock (_gate)
            {
                _historical.TryGetValue(id, out req);
                _streams.TryGetValue(id, out stream);
                _tickStreams.TryGetValue(id, out tickStream);
            }
            var ex = new InvalidOperationException($"IB {errorCode}: {errorMsg}");
            req?.Tcs.TrySetException(ex);
            stream?.Writer.TryComplete(ex);
            tickStream?.Channel.Writer.TryComplete(ex);
        }
    }

    public override void connectionClosed()
    {
        _logger.LogWarning("IB connection closed");
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }
}
#endif
