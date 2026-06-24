using System.Globalization;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Infrastructure.MarketData.Archive.Lake;

/// <summary>
/// IHostedService that, once per day at the configured UTC hour, exports the most recently closed
/// period to the local Parquet lake. Same idle-cheap, sleep-tolerant design as
/// <see cref="ArchiveScheduleService"/>: it ticks every 15 minutes, returns early unless enabled
/// and on the right hour, and relies on the exporter's append-only idempotence so same-day re-runs
/// are no-ops. Independent of the Telegram offloader — both can run.
/// </summary>
internal sealed class ParquetLakeExportService : IHostedService, IDisposable
{
    private readonly LocalParquetLakeExporter _exporter;
    private readonly IOptionsMonitor<ParquetLakeOptions> _options;
    private readonly ILogger<ParquetLakeExportService> _logger;
    private Timer? _timer;
    private CancellationTokenSource? _runCts;

    public ParquetLakeExportService(
        LocalParquetLakeExporter exporter,
        IOptionsMonitor<ParquetLakeOptions> options,
        ILogger<ParquetLakeExportService> logger)
    {
        _exporter = exporter;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(_ => _ = TickAsync(_runCts.Token), null,
            dueTime: TimeSpan.FromSeconds(45),
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
            if (now.Hour != opts.DailyCheckHourUtc) return;

            var (fromUtc, toUtc) = ArchivePeriodMath.ClosedPeriod(now, opts.Period);
            var label = PeriodLabel(fromUtc, opts.Period);
            var root = ResolveRoot(opts.RootDirectory);

            _logger.LogInformation(
                "Parquet lake tick: exporting [{From:s} → {To:s}) labelled {Label} to {Root}",
                fromUtc, toUtc, label, root);

            var progress = new Progress<string>(line => _logger.LogInformation("[lake] {Line}", line));
            await _exporter.ExportRangeAsync(fromUtc, toUtc, root, label, opts.Tables, progress, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parquet lake export tick failed");
        }
    }

    /// <summary>Sortable, glob-safe period label: <c>yyyy-MM-dd</c> (Monday) for weekly,
    /// <c>yyyy-MM</c> for monthly.</summary>
    private static string PeriodLabel(DateTime fromUtc, ArchivePeriod period) => period switch
    {
        ArchivePeriod.Weekly => fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ArchivePeriod.Monthly => fromUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static string ResolveRoot(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DaxAlgo Terminal", "parquet-lake");

    public void Dispose()
    {
        _timer?.Dispose();
        _runCts?.Dispose();
    }
}
