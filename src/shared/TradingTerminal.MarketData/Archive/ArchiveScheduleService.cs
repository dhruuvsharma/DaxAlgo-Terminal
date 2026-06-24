using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>
/// IHostedService that wakes once per day at the configured UTC hour and, on every period
/// rollover (weekly Monday-00:00 UTC, or first-of-month UTC), kicks off an archive of the period
/// that just closed. Idempotent — the archiver itself rejects duplicate ranges, so re-runs on the
/// same day are safe.
///
/// Deliberately checks every day rather than scheduling one fire at the rollover boundary: it
/// survives the machine being asleep over the boundary, time-zone shifts, and config changes
/// (the period can be toggled live in settings).
/// </summary>
internal sealed class ArchiveScheduleService : IHostedService, IDisposable
{
    private readonly IMarketDataArchiver _archiver;
    private readonly IOptionsMonitor<ArchiveOptions> _options;
    private readonly ILogger<ArchiveScheduleService> _logger;
    private Timer? _timer;
    private CancellationTokenSource? _runCts;

    public ArchiveScheduleService(
        IMarketDataArchiver archiver,
        IOptionsMonitor<ArchiveOptions> options,
        ILogger<ArchiveScheduleService> logger)
    {
        _archiver = archiver;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Fire immediately on startup (the archiver's idempotence catches the same-day re-run)
        // and then once per hour. We re-check the configured hour ourselves so a config change
        // takes effect at next tick.
        _timer = new Timer(_ => _ = TickAsync(_runCts.Token), null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _runCts?.Cancel();
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled) return;

            var now = DateTime.UtcNow;
            if (now.Hour != opts.DailyCheckHourUtc) return;  // wrong hour for today's run

            // Determine the most recently *closed* period.
            var (fromUtc, toUtc) = ArchivePeriodMath.ClosedPeriod(now, opts.Period);

            var target = opts.DefaultTargetKind == "chat" && !string.IsNullOrWhiteSpace(opts.DefaultTargetChatRef)
                ? ArchiveTarget.Chat(opts.DefaultTargetChatRef!)
                : ArchiveTarget.SavedMessages;

            _logger.LogInformation("Scheduled archive tick: range [{From:s} → {To:s}), target {Target}",
                fromUtc, toUtc, target.IsSavedMessages ? "Saved Messages" : target.ChatRef);

            var progress = new Progress<string>(line => _logger.LogInformation("[archive] {Line}", line));
            await _archiver.ArchiveRangeAsync(fromUtc, toUtc, target, progress, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled archive tick failed");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _runCts?.Dispose();
    }
}
