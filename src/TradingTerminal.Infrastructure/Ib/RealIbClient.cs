#if HAS_IBAPI
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IBApi;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Real Interactive Brokers client. Compiled only when <c>lib/IBApi.dll</c> is present.
/// Wraps <see cref="EClientSocket"/> + <see cref="EWrapper"/> behind <see cref="IIbClient"/>;
/// callbacks are pumped off the IB reader thread into channels keyed by request id.
/// </summary>
public sealed class RealIbClient : IIbClient, EWrapper
{
    private readonly ILogger<RealIbClient> _logger;
    private readonly EReaderSignal _signal = new EReaderMonitorSignal();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    private EClientSocket? _client;
    private EReader? _reader;
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
        _client = new EClientSocket(this, _signal);
        _connectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _client.eConnect(host, port, clientId);

        _reader = new EReader(_client, _signal);
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

        // nextValidId callback completes _connectTcs.
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
        var endDateTime = string.Empty; // up to "now"
        var durationStr = ToIbDuration(duration);
        _client.reqHistoricalData(reqId, ibContract,
            endDateTime, durationStr, barSize.ToIbString(),
            "TRADES", useRTH: 1, formatDate: 1, keepUpToDate: false, chartOptions: null);

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
            string.Empty, ToIbDuration(TimeSpan.FromHours(1)), barSize.ToIbString(),
            "TRADES", useRTH: 1, formatDate: 1, keepUpToDate: true, chartOptions: null);

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
        // IB returns either yyyyMMdd HH:mm:ss timezone or epoch. With formatDate=1 we get the former.
        DateTime tsUtc;
        if (long.TryParse(b.Time, out var epoch))
        {
            tsUtc = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }
        else
        {
            // "yyyyMMdd  HH:mm:ss"
            var s = b.Time.Trim();
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var dt = DateTime.ParseExact(parts.Length >= 2 ? parts[0] + " " + parts[1] : parts[0],
                parts.Length >= 2 ? "yyyyMMdd HH:mm:ss" : "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            tsUtc = dt.ToUniversalTime();
        }
        return new Bar(tsUtc, b.Open, b.High, b.Low, b.Close, b.Volume);
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

    // ---------- EWrapper ----------

    public void nextValidId(int orderId)
    {
        Interlocked.Exchange(ref _nextRequestId, Math.Max(_nextRequestId, orderId));
        _connectTcs?.TrySetResult();
    }

    public void historicalData(int reqId, IBApi.Bar bar)
    {
        HistoricalRequest? req = null;
        Channel<Bar>? stream = null;
        lock (_gate)
        {
            _historical.TryGetValue(reqId, out req);
            _streams.TryGetValue(reqId, out stream);
        }
        var parsed = ParseBar(bar);
        req?.Bars.Add(parsed);
        stream?.Writer.TryWrite(parsed);
    }

    public void historicalDataEnd(int reqId, string startDateStr, string endDateStr)
    {
        HistoricalRequest? req;
        lock (_gate) { _historical.TryGetValue(reqId, out req); _historical.Remove(reqId); }
        req?.Tcs.TrySetResult(req.Bars);
    }

    public void historicalDataUpdate(int reqId, IBApi.Bar bar)
    {
        Channel<Bar>? stream;
        lock (_gate) _streams.TryGetValue(reqId, out stream);
        stream?.Writer.TryWrite(ParseBar(bar));
    }

    public void error(Exception e) =>
        _logger.LogError(e, "IB error");

    public void error(string str) =>
        _logger.LogWarning("IB error: {Message}", str);

    public void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
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

    public void connectionClosed()
    {
        _logger.LogWarning("IB connection closed");
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    // -- Unused EWrapper methods (no-ops). --
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
    public void tickSize(int tickerId, int field, decimal size) { }
    public void tickString(int tickerId, int field, string value) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, IBApi.Contract contract, IBApi.Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(IBApi.Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetails(int reqId, IBApi.Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void managedAccounts(string accountsList) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count) { }
    public void currentTime(long time) { }
    public void fundamentalData(int reqId, string data) { }
    public void deltaNeutralValidation() { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void position(string account, IBApi.Contract contract, decimal pos, double avgCost) { }
    public void positionEnd() { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void connectAck() { }
    public void positionMulti(int requestId, string account, string modelCode, IBApi.Contract contract, decimal pos, double avgCost) { }
    public void positionMultiEnd(int requestId) { }
    public void accountUpdateMulti(int requestId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int requestId) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(IBApi.Contract contract, IBApi.Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void replaceFAEnd(int reqId, string text) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions) { }
    public void userInfo(int reqId, string whiteBrandingId) { }
}
#endif
