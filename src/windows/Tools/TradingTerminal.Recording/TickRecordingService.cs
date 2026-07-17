using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Archive;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.Recording;

/// <summary>
/// The live market-data recorder, as an app-lifetime background service.
///
/// <para>It records <b>through the canonical pipeline</b>, not around it: for each watched instrument
/// it takes ref-counted <see cref="IMarketDataIngest"/> subscriptions, and the pipeline's own store
/// writer persists every stream — L1 quotes + L2 depth (<see cref="IMarketDataIngest.Subscribe"/>),
/// 1-minute bars (<see cref="IMarketDataIngest.SubscribeBars"/>) and the trade tape
/// (<see cref="IMarketDataIngest.SubscribeTrades"/>, where the broker has one). Nothing here writes
/// files; the recorder decides *what* is captured and the store decides *how*. The per-broker SQLite
/// backend (the default) keeps each stream in its own <c>marketdata-{broker}-{bars|l1|trades|l2}.db</c>.</para>
///
/// <para><b>L3 / market-by-order is not recorded because nothing in this build produces it</b> — there's
/// no broker seam, no store stream, and no feed. The panel dims that chip; it is not a silent no-op.</para>
///
/// <para>Lifetime: a singleton owned by the host, so recording survives the panel window being closed —
/// that is the whole point of the rewrite. The panel is a view onto this service, never its owner.</para>
/// </summary>
public sealed partial class TickRecordingService : ObservableObject, IHostedService, IDisposable
{
    /// <summary>How often auto-upload asks the archiver to ship whatever is pending. The user asked for
    /// hourly; what actually leaves the machine each tick is every <i>closed</i> archive period
    /// (weekly/monthly per <see cref="ArchiveOptions.Period"/>), because that's the window the manifest
    /// and the coverage view are keyed on.</summary>
    private static readonly TimeSpan OffloadInterval = TimeSpan.FromHours(1);

    /// <summary>Bars are recorded at the finest cadence the pipeline aggregates; anything coarser is
    /// derivable downstream from these plus the tape.</summary>
    private const BarSize RecordedBarSize = BarSize.OneMinute;

    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataHub _hub;
    private readonly IBrokerSelector _selector;
    private readonly IMarketDataArchiver _archiver;
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOptions;
    private readonly IOptionsMonitor<TelegramArchiveOptions> _telegramOptions;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<TickRecordingService> _logger;

    private readonly object _gate = new();
    private Timer? _offloadTimer;
    private CancellationTokenSource? _offloadCts;
    private DateTime _startedAtUtc;
    private volatile bool _offloadInFlight;

    public TickRecordingService(
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        IBrokerSelector selector,
        IMarketDataArchiver archiver,
        IOptionsMonitor<ArchiveOptions> archiveOptions,
        IOptionsMonitor<TelegramArchiveOptions> telegramOptions,
        InMemoryLogSink log,
        ILogger<TickRecordingService> logger)
    {
        _ingest = ingest;
        _hub = hub;
        _selector = selector;
        _archiver = archiver;
        _archiveOptions = archiveOptions;
        _telegramOptions = telegramOptions;
        _log = log;
        _logger = logger;
    }

    /// <summary>The watchlist. Mutated only from the UI thread (via the panel), so the collection needs
    /// no extra synchronisation; the counters inside each entry are the thread-crossing part.</summary>
    public ObservableCollection<RecorderEntry> Instruments { get; } = new();

    [ObservableProperty] private bool _isRecording;

    /// <summary>Ship recorded data to Telegram on an hourly cadence via the existing archive offloader.</summary>
    [ObservableProperty] private bool _autoUploadTelegram;

    /// <summary>Whether an upload also prunes the uploaded rows from the local store. Off by default:
    /// the recorder's job is to *build* a local tape, so Telegram is a second copy, not a move. On, this
    /// is the archive's original "free the local disk" behaviour.</summary>
    [ObservableProperty] private bool _deleteLocalAfterUpload;

    [ObservableProperty] private string _status = "Idle — add instruments and press Record.";

    [ObservableProperty] private string _elapsed = "00:00:00";

    public bool HasInstruments => Instruments.Count > 0;

    // ── Hosted lifetime ──────────────────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var saved = RecorderWatchlistStore.Load();
        AutoUploadTelegram = saved.AutoUploadTelegram;
        DeleteLocalAfterUpload = saved.DeleteLocalAfterUpload;

        var universe = SignalInstrumentCatalog.All;
        foreach (var item in saved.Items)
        {
            var instrument = universe.FirstOrDefault(i =>
                string.Equals(i.Contract.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase));
            if (instrument is null) continue; // the universe changed under us — drop the row quietly
            BrokerKind? broker = Enum.TryParse<BrokerKind>(item.Broker, out var b) ? b : null;
            Instruments.Add(new RecorderEntry(instrument, broker));
        }

        if (Instruments.Count > 0)
            Status = $"Idle — {Instruments.Count} instrument(s) queued. Press Record.";
        OnPropertyChanged(nameof(HasInstruments));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopRecording("App shutting down.");
        return Task.CompletedTask;
    }

    // ── Watchlist ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Adds an instrument. No-op when it's already watched. Wires it up immediately when a
    /// recording is already running, so adding mid-session doesn't need a stop/start.</summary>
    public void Add(SignalInstrument instrument)
    {
        if (Instruments.Any(e => string.Equals(e.Symbol, instrument.Contract.Symbol, StringComparison.OrdinalIgnoreCase)))
            return;

        var entry = new RecorderEntry(instrument, instrument.Broker);
        Instruments.Add(entry);
        OnPropertyChanged(nameof(HasInstruments));
        Persist();

        if (IsRecording) StartEntry(entry);
    }

    public void Remove(RecorderEntry entry)
    {
        entry.DisposeSubscriptions();
        Instruments.Remove(entry);
        OnPropertyChanged(nameof(HasInstruments));
        Persist();
        if (IsRecording && Instruments.Count == 0) StopRecording("Last instrument removed.");
    }

    // ── Recording ────────────────────────────────────────────────────────────────────────────────

    public void ToggleRecording()
    {
        if (IsRecording) StopRecording("Stopped by user.");
        else StartRecording();
    }

    public void StartRecording()
    {
        if (IsRecording) return;
        if (Instruments.Count == 0)
        {
            Status = "Add at least one instrument first.";
            return;
        }
        if (_selector.Connected.Count == 0)
        {
            Status = "No broker is connected — connect one from the login screen.";
            _log.Append("Recorder", "Warning", "Record refused: no broker connected.");
            return;
        }

        _startedAtUtc = DateTime.UtcNow;
        IsRecording = true;

        foreach (var entry in Instruments) StartEntry(entry);

        var live = Instruments.Count(e => e.IsLive);
        Status = $"Recording {live} instrument(s) → canonical store.";
        _log.Append("Recorder", "Info", $"Recording started for {live} instrument(s).");
        _logger.LogInformation("Recording started for {Count} instrument(s)", live);

        ArmOffloadTimer();
    }

    public void StopRecording(string reason)
    {
        if (!IsRecording) return;
        IsRecording = false;

        foreach (var entry in Instruments) entry.DisposeSubscriptions();

        DisarmOffloadTimer();
        Status = $"Idle — {reason}";
        _log.Append("Recorder", "Info", $"Recording stopped. {reason}");
        _logger.LogInformation("Recording stopped: {Reason}", reason);
    }

    /// <summary>Subscribes one row to every stream its serving broker can actually provide. Each
    /// <see cref="IMarketDataIngest"/> handle is the pipeline's ref-counted pump (which persists);
    /// each hub handle only bumps a counter so the panel can show the row is alive.</summary>
    private void StartEntry(RecorderEntry entry)
    {
        entry.DisposeSubscriptions();
        entry.ResetCounters();

        var broker = ResolveBroker(entry);
        if (broker is null)
        {
            entry.Status = "No connected broker for this instrument.";
            return;
        }

        entry.ActiveBroker = broker;
        var contract = entry.Instrument.Contract;

        try
        {
            entry.Id = _ingest.Resolve(contract, broker.Value);

            // L1 + L2 depth (one ingest handle drives both pumps), and 1-minute bars.
            entry.Subscriptions.Add(_ingest.Subscribe(contract, broker.Value));
            entry.Subscriptions.Add(_ingest.SubscribeBars(contract, broker.Value, RecordedBarSize));

            // Tape is opt-in per broker — the others throw NotSupportedException rather than stream.
            if (entry.SupportsTape)
                entry.Subscriptions.Add(_ingest.SubscribeTrades(contract, broker.Value));

            // Counters. Interlocked off the feed thread; the panel timer publishes them.
            entry.Subscriptions.Add(_hub.Quotes(entry.Id).Subscribe(_ => Interlocked.Increment(ref entry.QuotesRaw)));
            entry.Subscriptions.Add(_hub.Bars(entry.Id, RecordedBarSize).Subscribe(_ => Interlocked.Increment(ref entry.BarsRaw)));
            if (entry.SupportsDepth)
                entry.Subscriptions.Add(_hub.Depth(entry.Id).Subscribe(_ => Interlocked.Increment(ref entry.DepthRaw)));
            if (entry.SupportsTape)
                entry.Subscriptions.Add(_hub.Trades(entry.Id).Subscribe(_ => Interlocked.Increment(ref entry.TradesRaw)));

            entry.IsLive = true;
            entry.Status = DescribeStreams(entry);
        }
        catch (Exception ex)
        {
            entry.DisposeSubscriptions();
            entry.Status = $"Failed: {ex.Message}";
            _log.Append("Recorder", "Warning", $"{entry.DisplayName}: {ex.Message}");
            _logger.LogWarning(ex, "Recorder failed to subscribe {Symbol}", entry.Symbol);
        }
    }

    private static string DescribeStreams(RecorderEntry entry)
    {
        var streams = new List<string> { "L1", "bars" };
        if (entry.SupportsDepth) streams.Add("L2");
        if (entry.SupportsTape) streams.Add("tape");
        var text = $"Recording {string.Join(" · ", streams)}";
        if (!entry.SupportsTape) text += " — no tape on this broker";
        return text;
    }

    /// <summary>The pinned broker when it's connected, else any connected broker.</summary>
    private BrokerKind? ResolveBroker(RecorderEntry entry)
    {
        if (entry.PinnedBroker is { } pinned && _selector.IsConnected(pinned)) return pinned;
        var connected = _selector.Connected;
        return connected.Count > 0 ? connected[0] : null;
    }

    // ── Auto-upload ──────────────────────────────────────────────────────────────────────────────

    partial void OnAutoUploadTelegramChanged(bool value)
    {
        Persist();
        if (value) ApplyArchivePreferences();
        if (IsRecording)
        {
            if (value) ArmOffloadTimer();
            else DisarmOffloadTimer();
        }
    }

    partial void OnDeleteLocalAfterUploadChanged(bool value)
    {
        Persist();
        if (AutoUploadTelegram) ApplyArchivePreferences();
    }

    /// <summary>
    /// Points the archive subsystem at what the recorder actually captures, through the same user file
    /// the Archive Settings dialog writes — so the change is visible there rather than hidden runtime
    /// state, and <see cref="IOptionsMonitor{T}"/> picks it up without a restart.
    ///
    /// <para>Two defaults would otherwise silently drop recorded data: <see cref="ArchiveOptions.Tables"/>
    /// ships Quotes+Bars only (no tape, no L2), and <see cref="ArchiveOptions.DeleteLocalAfterArchive"/>
    /// is true (an upload would prune the tape we just recorded).</para>
    /// </summary>
    private void ApplyArchivePreferences()
    {
        try
        {
            var current = _archiveOptions.CurrentValue;
            var next = new ArchiveOptions
            {
                Enabled = true,
                Period = current.Period,
                Tables = ArchiveTables.Quotes | ArchiveTables.Bars | ArchiveTables.Trades | ArchiveTables.Depth,
                DailyCheckHourUtc = current.DailyCheckHourUtc,
                MaxPartBytes = current.MaxPartBytes,
                VerifyAfterUpload = current.VerifyAfterUpload,
                DeleteLocalAfterArchive = DeleteLocalAfterUpload,
                DefaultTargetKind = current.DefaultTargetKind,
                DefaultTargetChatRef = current.DefaultTargetChatRef,
                StagingDirectory = current.StagingDirectory,
                ManifestDatabasePath = current.ManifestDatabasePath,
            };
            ArchiveUserFile.Save(next, _telegramOptions.CurrentValue);
            _log.Append("Recorder", "Info",
                $"Archive set to ship quotes+bars+trades+depth; local data {(DeleteLocalAfterUpload ? "pruned" : "kept")} after upload.");
        }
        catch (Exception ex)
        {
            _log.Append("Recorder", "Warning", $"Couldn't update archive settings: {ex.Message}");
            _logger.LogWarning(ex, "Recorder failed to write archive preferences");
        }
    }

    private void ArmOffloadTimer()
    {
        if (!AutoUploadTelegram) return;
        lock (_gate)
        {
            DisarmOffloadTimer();
            _offloadCts = new CancellationTokenSource();
            var ct = _offloadCts.Token;
            _offloadTimer = new Timer(_ => _ = OffloadTickAsync(ct), null, OffloadInterval, OffloadInterval);
        }
    }

    private void DisarmOffloadTimer()
    {
        lock (_gate)
        {
            _offloadCts?.Cancel();
            _offloadCts?.Dispose();
            _offloadCts = null;
            _offloadTimer?.Dispose();
            _offloadTimer = null;
        }
    }

    /// <summary>One hourly upload attempt. Re-entrancy guarded: an offload can easily run longer than
    /// the interval (it uploads, re-downloads to verify sha256, then prunes), and two concurrent runs
    /// would fight over the same manifest rows.</summary>
    private async Task OffloadTickAsync(CancellationToken ct)
    {
        if (!AutoUploadTelegram || _offloadInFlight || ct.IsCancellationRequested) return;
        _offloadInFlight = true;
        try
        {
            var progress = new Progress<string>(m => _log.Append("Recorder", "Info", m));
            var result = await _archiver.OffloadPendingAsync(progress, ct).ConfigureAwait(false);
            _log.Append("Recorder", "Info", $"Hourly auto-upload finished: {result}");
        }
        catch (OperationCanceledException) { /* recording stopped mid-upload */ }
        catch (Exception ex)
        {
            _log.Append("Recorder", "Warning", $"Auto-upload failed: {ex.Message}");
            _logger.LogWarning(ex, "Recorder auto-upload failed");
        }
        finally
        {
            _offloadInFlight = false;
        }
    }

    // ── Misc ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Refreshes the elapsed clock. Driven by the panel's timer so no timer runs while nobody
    /// is looking at it.</summary>
    public void RefreshElapsed() =>
        Elapsed = IsRecording ? (DateTime.UtcNow - _startedAtUtc).ToString(@"hh\:mm\:ss") : "00:00:00";

    private void Persist() => RecorderWatchlistStore.Save(new RecorderWatchlist(
        Instruments.Select(e => e.ToWatchlistItem()).ToList(),
        AutoUploadTelegram,
        DeleteLocalAfterUpload));

    public void Dispose()
    {
        DisarmOffloadTimer();
        foreach (var entry in Instruments) entry.DisposeSubscriptions();
    }
}
