using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Deletes market data older than its configured window, on a timer.
///
/// <para>Before this existed the only thing that ever deleted anything was the Telegram archive's
/// "prune after upload". A user who never archived — the default — had a store that grew forever, and
/// depth grew fastest of all.</para>
///
/// <para>Deliberately a <b>timer</b> rather than a one-shot at startup, because the store's own depth
/// prune already was a one-shot at startup and that is exactly why a terminal left running for a week
/// never pruned again.</para>
///
/// <para><b>Known limit — the per-broker store.</b> <c>PerBrokerSqliteMarketDataStore</c> opens one
/// SQLite file per broker per stream <em>lazily</em>, and its extent and delete calls only reach the
/// files it has already opened. So a sweep prunes the brokers active in this session; a file left
/// behind by a broker the user no longer connects to is never opened and never pruned. That file is
/// static — nothing is writing to it — so it costs a fixed amount of disk rather than unbounded
/// growth, which is why this is documented rather than fixed by force-opening every file on disk:
/// doing that would start a writer thread per broker per stream for data nobody is using.</para>
///
/// <para>All the decisions live in <see cref="MarketDataRetentionPolicy"/>; this only supplies the
/// clock, the extent, the archive floor, and the store calls.</para>
/// </summary>
internal sealed class MarketDataRetentionService : IHostedService, IDisposable
{
    /// <summary>Long enough for startup to settle before touching the database.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>Floor on the configured interval, so a bad config cannot turn this into a hot loop.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

    private readonly IMarketDataStore _store;
    private readonly IOptionsMonitor<MarketDataStoreOptions> _options;
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOptions;
    private readonly IMarketDataArchiver? _archiver;
    private readonly ILogger<MarketDataRetentionService> _logger;
    private CancellationTokenSource? _lifetime;
    private Timer? _timer;
    private int _running;

    public MarketDataRetentionService(
        IMarketDataStore store,
        IOptionsMonitor<MarketDataStoreOptions> options,
        IOptionsMonitor<ArchiveOptions> archiveOptions,
        ILogger<MarketDataRetentionService> logger,
        IMarketDataArchiver? archiver = null)
    {
        _store = store;
        _options = options;
        _archiveOptions = archiveOptions;
        _archiver = archiver;
        _logger = logger;
    }

    /// <summary>Rows deleted since the process started. Surfaced for diagnostics and tests.</summary>
    public long TotalRowsDeleted { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = Interval();
        _timer = new Timer(_ => _ = SweepAsync(_lifetime.Token), null, StartupDelay, interval);
        _logger.LogInformation(
            "Market-data retention: first sweep in {Delay}, then every {Interval}.",
            StartupDelay,
            interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _lifetime?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs one sweep. Public so a settings screen can force one after the user shortens a window
    /// instead of making them wait for the timer.
    /// </summary>
    public async Task<long> SweepAsync(CancellationToken ct)
    {
        // One sweep at a time. A slow first delete must not have the timer stacking runs behind it.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return 0L;

        try
        {
            var options = _options.CurrentValue;
            if (!options.RetentionSweepEnabled)
                return 0L;

            var extent = await _store.GetDataExtentAsync(ct).ConfigureAwait(false);
            var floor = await ArchiveFloorAsync(ct).ConfigureAwait(false);
            var plan = MarketDataRetentionPolicy.Plan(options, DateTime.UtcNow, extent.EarliestUtc, floor);
            if (plan.Count == 0)
                return 0L;

            var deleted = 0L;
            foreach (var cut in plan)
            {
                ct.ThrowIfCancellationRequested();
                deleted += await DeleteAsync(cut, ct).ConfigureAwait(false);
            }

            TotalRowsDeleted += Math.Max(deleted, 0L);
            if (deleted != 0L)
            {
                // -1 comes back from QuestDB, which drops whole day partitions and cannot report a row
                // count. Reporting "some" beats reporting a negative number as if it were one.
                _logger.LogInformation(
                    "Market-data retention deleted {Rows} across {Streams} stream(s).",
                    deleted < 0 ? "an unreported number of rows" : $"{deleted:N0} rows",
                    plan.Count);
            }

            foreach (var cut in plan.Where(item => item.ClampedByPendingArchive))
            {
                _logger.LogInformation(
                    "Retention for {Stream} stopped at {Cutoff:u} because the archive has not shipped that far yet.",
                    cut.Stream,
                    cut.ToUtc);
            }

            return deleted;
        }
        catch (OperationCanceledException)
        {
            return 0L;
        }
        catch (Exception ex)
        {
            // A failed sweep is a disk-space problem, not a trading problem. Log it and try again on
            // the next tick rather than taking the host down.
            _logger.LogWarning(ex, "Market-data retention sweep failed; it will be retried.");
            return 0L;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private Task<long> DeleteAsync(MarketDataRetentionCut cut, CancellationToken ct) => cut.Stream switch
    {
        MarketDataStream.Quotes => _store.DeleteQuotesInRangeAsync(cut.FromUtc, cut.ToUtc, ct),
        MarketDataStream.Trades => _store.DeleteTradesInRangeAsync(cut.FromUtc, cut.ToUtc, ct),
        MarketDataStream.Bars => _store.DeleteBarsInRangeAsync(cut.FromUtc, cut.ToUtc, ct),
        MarketDataStream.Depth => _store.DeleteDepthInRangeAsync(cut.FromUtc, cut.ToUtc, ct),
        _ => Task.FromResult(0L),
    };

    /// <summary>
    /// How far the archive still needs the data kept, or null when it does not.
    ///
    /// <para>A failure here returns null rather than blocking the sweep: an unreadable manifest should
    /// not mean the disk fills up. The window is generous enough that one skipped clamp cannot delete
    /// something the archiver was about to ship on the very next tick.</para>
    /// </summary>
    private async Task<DateTime?> ArchiveFloorAsync(CancellationToken ct)
    {
        if (_archiver is null || !_options.CurrentValue.RespectPendingArchives)
            return null;

        try
        {
            var archiveEnabled = _archiveOptions.CurrentValue.Enabled;
            if (!archiveEnabled)
                return null;

            var succeeded = await _archiver.ListArchivesAsync(maxRows: 1, ct: ct).ConfigureAwait(false);
            var coverage = await _archiver.GetCoverageAsync(ct).ConfigureAwait(false);

            return MarketDataRetentionPolicy.ArchiveFloor(
                archiveEnabled,
                succeeded.Count > 0,
                coverage.Where(window => !window.Offloaded).Select(window => window.FromUtc));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read archive coverage; retention will not be clamped this sweep.");
            return null;
        }
    }

    private TimeSpan Interval()
    {
        var hours = _options.CurrentValue.RetentionSweepIntervalHours;
        var interval = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromHours(6);
        return interval < MinimumInterval ? MinimumInterval : interval;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _lifetime?.Dispose();
    }
}
