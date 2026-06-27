using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Regime;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Orchestrates the regime feed: fan out to every source in parallel, fold the results into a
/// <see cref="RegimeInputs"/>, run the pure <see cref="MarketRegimeCalculator"/>, then publish
/// the snapshot. Each source swallows its own failures (returning null/empty), so a single dead
/// endpoint degrades one category instead of failing the whole composite.
/// </summary>
internal sealed class MarketRegimeService : IMarketRegimeProvider
{
    // FRED series ids consumed by credit / liquidity / macro and the header metrics.
    private const string HighYieldOas = "BAMLH0A0HYM2";
    private const string InvGradeOas = "BAMLC0A0CM";
    private const string M2 = "M2SL";
    private const string FedBalanceSheet = "WALCL";
    private const string Sofr = "SOFR";
    private const string FedFunds = "FEDFUNDS";
    private const string Curve10y2y = "T10Y2Y";
    private const string Unemployment = "UNRATE";
    private const string Yield10y = "DGS10";

    private static readonly string[] SectorEtfs =
        { "XLK", "XLF", "XLE", "XLV", "XLY", "XLP", "XLI", "XLB", "XLU", "XLRE", "XLC" };

    private readonly YahooChartClient _yahoo;
    private readonly FredClient _fred;
    private readonly CnnFearGreedClient _cnn;
    private readonly AaiiSentimentClient _aaii;
    private readonly IOptionsMonitor<MarketRegimeOptions> _options;
    private readonly ILogger<MarketRegimeService> _logger;

    private readonly BehaviorSubject<MarketRegimeSnapshot> _subject = new(MarketRegimeSnapshot.Empty);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public MarketRegimeService(
        YahooChartClient yahoo,
        FredClient fred,
        CnnFearGreedClient cnn,
        AaiiSentimentClient aaii,
        IOptionsMonitor<MarketRegimeOptions> options,
        ILogger<MarketRegimeService> logger)
    {
        _yahoo = yahoo;
        _fred = fred;
        _cnn = cnn;
        _aaii = aaii;
        _options = options;
        _logger = logger;
    }

    public MarketRegimeSnapshot Current => _subject.Value;

    public IObservable<MarketRegimeSnapshot> Updates => _subject.AsObservable();

    public async Task<MarketRegimeSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var o = _options.CurrentValue;
            var inputs = await GatherAsync(o, ct).ConfigureAwait(false);
            var previous = _subject.Value.Unavailable ? (double?)null : _subject.Value.CompositeScore;
            var snapshot = MarketRegimeCalculator.Compute(inputs, previous, DateTime.UtcNow);
            _subject.OnNext(snapshot);
            _logger.LogInformation("Market regime: {Score:F1} ({Label})", snapshot.CompositeScore, snapshot.Label);
            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Market regime refresh failed");
            return _subject.Value;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<RegimeInputs> GatherAsync(MarketRegimeOptions o, CancellationToken ct)
    {
        // --- Yahoo (always on): index, vol, cross-asset ETFs, sectors ---
        var yahooSymbols = new[] { "^GSPC", "^VIX", "^VIX9D", "^VIX3M", "^SKEW", "GLD", "TLT", "HYG", "SPY", "RSP", "DX-Y.NYB" }
            .Concat(SectorEtfs)
            .ToArray();
        var yahooTasks = yahooSymbols.ToDictionary(s => s, s => _yahoo.GetAsync(s, ct));
        await Task.WhenAll(yahooTasks.Values).ConfigureAwait(false);
        YahooChartClient.Series Y(string s) => yahooTasks[s].Result;

        // --- Optional sentiment scrapers ---
        var cnnTask = o.UseCnnFearGreed ? _cnn.GetAsync(ct) : Task.FromResult<int?>(null);
        var aaiiTask = o.UseAaiiSentiment ? _aaii.GetAsync(ct) : Task.FromResult<AaiiSentimentClient.Sentiment?>(null);

        // --- Optional FRED series (need a key) ---
        var key = o.FredApiKey;
        Task<double[]> Fred(string id) => _fred.GetSeriesAsync(id, key, ct);
        var hyTask = Fred(HighYieldOas);
        var igTask = Fred(InvGradeOas);
        var m2Task = Fred(M2);
        var walclTask = Fred(FedBalanceSheet);
        var sofrTask = Fred(Sofr);
        var fedTask = Fred(FedFunds);
        var curveTask = Fred(Curve10y2y);
        var unrateTask = Fred(Unemployment);
        var y10Task = Fred(Yield10y);

        await Task.WhenAll(cnnTask, aaiiTask).ConfigureAwait(false);
        await Task.WhenAll(hyTask, igTask, m2Task, walclTask, sofrTask, fedTask, curveTask, unrateTask, y10Task)
            .ConfigureAwait(false);

        var aaii = aaiiTask.Result;
        var sofrSeries = sofrTask.Result;

        var sectors = SectorEtfs.ToDictionary(s => s, s => Y(s).Closes);

        return new RegimeInputs
        {
            Vix = Y("^VIX").Price,
            Vix9d = Y("^VIX9D").Price,
            Vix3m = Y("^VIX3M").Price,
            Skew = Y("^SKEW").Price,
            SpxCloses = Y("^GSPC").Closes,
            SpyCloses = Y("SPY").Closes,
            RspCloses = Y("RSP").Closes,
            GldCloses = Y("GLD").Closes,
            TltCloses = Y("TLT").Closes,
            DxyCloses = Y("DX-Y.NYB").Closes,
            HygPrice = Y("HYG").Price,
            TltPrice = Y("TLT").Price,
            SectorCloses = sectors,
            CnnFearGreed = cnnTask.Result,
            AaiiBull = aaii?.Bull,
            AaiiBear = aaii?.Bear,
            HighYieldOas = hyTask.Result,
            InvGradeOas = igTask.Result,
            M2 = m2Task.Result,
            FedBalanceSheet = walclTask.Result,
            FedFunds = fedTask.Result,
            Curve10y2y = curveTask.Result,
            Unemployment = unrateTask.Result,
            Yield10y = y10Task.Result,
            Sofr = sofrSeries.Length > 0 ? sofrSeries[^1] : null,
            // No free put/call feed wired in v1 → positioning rides on SKEW.
            PutCallRatio = null,
            // % above 200dma not sourced in v1 → breadth rides on RSP vs SPY.
            PctAbove200dma = null,
        };
    }
}
