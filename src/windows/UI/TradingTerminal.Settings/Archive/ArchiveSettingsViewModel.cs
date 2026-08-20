using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;
using TradingTerminal.UI;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Settings tab for the market-data archive: whether this machine stores the live feed at all,
/// Telegram credentials + login, the schedule + retention knobs, and a manual "Offload now" range
/// picker. Saving writes the per-user JSON; IOptionsMonitor surfaces the change to the running
/// schedule service.
/// </summary>
public sealed partial class ArchiveSettingsViewModel : ViewModelBase
{
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOpts;
    private readonly IOptionsMonitor<TelegramArchiveOptions> _telegramOpts;
    private readonly TelegramArchiveTransport _transport;
    private readonly IMarketDataArchiver _archiver;
    private readonly ILocalMarketDataPersistence? _localPersistence;
    private readonly IOptionsMonitor<MarketDataStoreOptions> _storeOpts;
    private readonly IMarketDataRetentionSweep? _retentionSweep;
    private readonly ILogger<ArchiveSettingsViewModel> _logger;

    public ArchiveSettingsViewModel(
        IOptionsMonitor<ArchiveOptions> archiveOpts,
        IOptionsMonitor<TelegramArchiveOptions> telegramOpts,
        TelegramArchiveTransport transport,
        IMarketDataArchiver archiver,
        ILogger<ArchiveSettingsViewModel> logger,
        IOptionsMonitor<MarketDataStoreOptions> storeOpts,
        IMarketDataRetentionSweep? retentionSweep = null,
        IMarketDataStore? store = null)
    {
        _archiveOpts = archiveOpts;
        _telegramOpts = telegramOpts;
        _transport = transport;
        _archiver = archiver;
        _logger = logger;
        // The store is the thing that can actually stop writing. Optional and matched by shape so a
        // host composing a store without the control still opens this screen.
        _storeOpts = storeOpts;
        _retentionSweep = retentionSweep;
        _localPersistence = store as ILocalMarketDataPersistence;
        _storeLiveDataLocally = _localPersistence?.IsPersistingLocally ?? false;
        _canStoreLiveDataLocally = _localPersistence is not null;

        LoadFromOptions();
        // Sane defaults for the manual offload — last completed week.
        var (from, to) = ArchivePeriodMath.ClosedPeriod(DateTime.UtcNow, ArchivePeriod.Weekly);
        ManualFromUtc = from;
        ManualToUtc = to;
    }

    // ----- Local storage -----

    /// <summary>
    /// Whether this machine keeps a copy of the live feed on disk.
    ///
    /// <para>Off means the terminal still works exactly as before — the feed flows, every window
    /// updates — but nothing is written down. What is given up is the warm start: the order book and
    /// volume footprint open with no replayed history, and every history request goes to the broker
    /// rather than the local cache.</para>
    /// </summary>
    [ObservableProperty]
    private bool _storeLiveDataLocally;

    /// <summary>False when this build composed a store with no runtime control, so the box is disabled
    /// rather than lying about having taken effect.</summary>
    [ObservableProperty]
    private bool _canStoreLiveDataLocally;

    /// <summary>
    /// Applied immediately, not on Save. A user who turns local storage OFF is usually doing it
    /// because they want the writes to stop now; making them press Save first would keep writing in
    /// the meantime. Save still records the choice so it survives a restart.
    /// </summary>
    partial void OnStoreLiveDataLocallyChanged(bool value)
    {
        if (_localPersistence is null)
            return;

        var applied = _localPersistence.SetLocalPersistence(value);
        StatusMessage = applied == value
            ? value
                ? "Storing market data on this device."
                : "Local market-data storage is off. Existing files are left alone."
            : "The store could not start writing — its backend is unreachable. The setting is saved and " +
              "applies once the backend is up.";
    }

    // ----- Retention -----

    /// <summary>Whether old data is deleted at all. Off means the store grows without bound.</summary>
    [ObservableProperty]
    private bool _retentionEnabled = true;

    /// <summary>Days of L1 quotes to keep. Nothing in the app reads stored quotes; the archive does.</summary>
    [ObservableProperty]
    private int _quoteRetentionDays;

    /// <summary>Days of trade prints to keep. The volume footprint warm-starts from at most 24 hours.</summary>
    [ObservableProperty]
    private int _tradeRetentionDays;

    /// <summary>Days of bars to keep. 0 = forever, and that is the default — bars are the history cache.</summary>
    [ObservableProperty]
    private int _barRetentionDays;

    /// <summary>Days of L2 depth to keep. The largest stream; the order book warm-starts from 30 minutes.</summary>
    [ObservableProperty]
    private int _depthRetentionDays;

    // ----- Telegram credentials -----
    [ObservableProperty] private int _apiId;
    [ObservableProperty] private string _apiHash = "";
    [ObservableProperty] private string _phoneNumber = "";
    [ObservableProperty] private string _telegramStatus = "Not logged in.";
    [ObservableProperty] private bool _isLoggedIn;

    // ----- Schedule + retention -----
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _period = "Weekly";
    [ObservableProperty] private bool _includeQuotes = true;
    [ObservableProperty] private bool _includeBars = true;
    [ObservableProperty] private bool _includeTrades = false;
    [ObservableProperty] private bool _includeDepth = false;
    [ObservableProperty] private int _dailyCheckHourUtc = 3;
    [ObservableProperty] private long _maxPartBytes = 1_900_000_000;
    [ObservableProperty] private bool _verifyAfterUpload = true;
    [ObservableProperty] private bool _deleteLocalAfterArchive = true;

    // ----- Default target -----
    [ObservableProperty] private string _defaultTargetKind = "saved";  // "saved" | "chat"
    [ObservableProperty] private string _defaultTargetChatRef = "";
    public bool DefaultTargetIsChat => string.Equals(DefaultTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
    partial void OnDefaultTargetKindChanged(string value) => OnPropertyChanged(nameof(DefaultTargetIsChat));

    // ----- Manual offload -----
    [ObservableProperty] private DateTime _manualFromUtc;
    [ObservableProperty] private DateTime _manualToUtc;
    [ObservableProperty] private string _manualTargetKind = "saved";
    [ObservableProperty] private string _manualTargetChatRef = "";
    public bool ManualTargetIsChat => string.Equals(ManualTargetKind, "chat", StringComparison.OrdinalIgnoreCase);
    partial void OnManualTargetKindChanged(string value) => OnPropertyChanged(nameof(ManualTargetIsChat));

    // ----- Status -----
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;

    public IReadOnlyList<string> PeriodOptions { get; } = new[] { "Weekly", "Monthly" };
    public IReadOnlyList<string> TargetKindOptions { get; } = new[] { "saved", "chat" };

    [RelayCommand]
    private async Task LoginToTelegramAsync()
    {
        // Pre-flight: catch missing fields here with a clear message instead of letting WTelegram
        // throw "value cannot be an empty string (Parameter: ...)" deep inside the auth flow.
        if (ApiId <= 0)
        {
            TelegramStatus = "Enter your Telegram api_id (a number from my.telegram.org/apps).";
            StatusMessage = TelegramStatus;
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiHash))
        {
            TelegramStatus = "Enter your Telegram api_hash (from my.telegram.org/apps).";
            StatusMessage = TelegramStatus;
            return;
        }
        if (string.IsNullOrWhiteSpace(PhoneNumber))
        {
            TelegramStatus = "Enter your phone number in international format (e.g. +91XXXXXXXXXX).";
            StatusMessage = TelegramStatus;
            return;
        }

        StatusMessage = "Connecting to Telegram…";
        Save(); // Persist creds first so the next app launch reads them from disk.
        IsBusy = true;
        try
        {
            // Pass the VM's in-memory values straight to the transport instead of relying on the
            // IOptionsMonitor.CurrentValue snapshot — its file-watcher debounce can still be holding
            // the stale empty values for a moment after Save() returned.
            var snap = new TradingTerminal.Core.Configuration.TelegramArchiveOptions
            {
                ApiId = ApiId,
                ApiHash = ApiHash.Trim(),
                PhoneNumber = PhoneNumber.Trim(),
                SessionFilePath = _telegramOpts.CurrentValue.SessionFilePath,
            };
            await Task.Run(() => _transport.EnsureConnectedAsync(snap, CancellationToken.None));
            IsLoggedIn = _transport.IsReady;
            TelegramStatus = IsLoggedIn ? "Connected." : "Login did not complete.";
            StatusMessage = TelegramStatus;
        }
        catch (OperationCanceledException ex)
        {
            TelegramStatus = $"Login canceled: {ex.Message}";
            StatusMessage = TelegramStatus;
            _logger.LogInformation("Telegram login canceled: {Reason}", ex.Message);
        }
        catch (Exception ex)
        {
            TelegramStatus = $"Login failed: {ex.Message}";
            StatusMessage = TelegramStatus;
            _logger.LogError(ex, "Telegram login failed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var snap = _archiveOpts.CurrentValue;
            var next = new ArchiveOptions
            {
                Enabled = Enabled,
                Period = Enum.TryParse<ArchivePeriod>(Period, out var p) ? p : ArchivePeriod.Weekly,
                Tables = ComposeTables(),
                DailyCheckHourUtc = Math.Clamp(DailyCheckHourUtc, 0, 23),
                MaxPartBytes = MaxPartBytes > 0 ? MaxPartBytes : 1_900_000_000,
                VerifyAfterUpload = VerifyAfterUpload,
                DeleteLocalAfterArchive = DeleteLocalAfterArchive,
                DefaultTargetKind = DefaultTargetKind ?? "saved",
                DefaultTargetChatRef = string.IsNullOrWhiteSpace(DefaultTargetChatRef) ? null : DefaultTargetChatRef.Trim(),
                StagingDirectory = snap.StagingDirectory,
                ManifestDatabasePath = snap.ManifestDatabasePath,
            };
            var tg = new TelegramArchiveOptions
            {
                ApiId = ApiId,
                ApiHash = ApiHash?.Trim() ?? "",
                PhoneNumber = PhoneNumber?.Trim() ?? "",
                SessionFilePath = _telegramOpts.CurrentValue.SessionFilePath,
            };
            ArchiveUserFile.Save(
                next,
                tg,
                CanStoreLiveDataLocally ? StoreLiveDataLocally : null,
                new MarketDataRetentionSettings(
                    RetentionEnabled,
                    Math.Max(QuoteRetentionDays, 0),
                    Math.Max(TradeRetentionDays, 0),
                    Math.Max(BarRetentionDays, 0),
                    Math.Max(DepthRetentionDays, 0)));
            StatusMessage = $"Saved to {ArchiveUserFile.Path}";
            // A shortened window should take effect now. Waiting for the next timer tick reads as
            // the setting having been ignored.
            if (RetentionEnabled)
                _ = SweepRetentionAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            _logger.LogError(ex, "Archive settings save failed");
        }
    }

    [RelayCommand]
    private async Task OffloadNowAsync()
    {
        if (ManualToUtc <= ManualFromUtc) { StatusMessage = "Manual offload: 'to' must be after 'from'."; return; }
        IsBusy = true;
        try
        {
            Save();
            var target = ManualTargetKind == "chat" && !string.IsNullOrWhiteSpace(ManualTargetChatRef)
                ? ArchiveTarget.Chat(ManualTargetChatRef.Trim())
                : ArchiveTarget.SavedMessages;
            StatusMessage = $"Offloading [{ManualFromUtc:s} â†’ {ManualToUtc:s})â€¦";
            var progress = new Progress<string>(line => StatusMessage = line);
            var result = await Task.Run(() => _archiver.ArchiveRangeAsync(
                DateTime.SpecifyKind(ManualFromUtc, DateTimeKind.Utc),
                DateTime.SpecifyKind(ManualToUtc, DateTimeKind.Utc),
                target, progress, CancellationToken.None));
            StatusMessage = $"Archive #{result.Entry.Id} complete ({result.Entry.Parts.Count} parts, " +
                            $"{result.Entry.RowsQuotes:n0} quotes, {result.Entry.RowsBars:n0} bars).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Offload failed: {ex.Message}";
            _logger.LogError(ex, "Manual offload failed");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Applies the saved retention windows immediately.
    ///
    /// <para>Not awaited by Save: a first sweep on a large store can take a while, and the settings
    /// screen must not freeze while it runs. Progress lands in the status line instead.</para>
    /// </summary>
    private async Task SweepRetentionAsync()
    {
        if (_retentionSweep is null)
            return;

        try
        {
            var deleted = await _retentionSweep.SweepAsync().ConfigureAwait(true);
            if (deleted > 0)
                StatusMessage = $"Saved. Deleted {deleted:N0} row(s) past their retention window.";
            else if (deleted < 0)
                StatusMessage = "Saved. Old data was dropped (this backend does not report a row count).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention sweep after save failed");
            StatusMessage = $"Saved, but the cleanup failed: {ex.Message}";
        }
    }

    private ArchiveTables ComposeTables()
    {
        var t = ArchiveTables.None;
        if (IncludeQuotes) t |= ArchiveTables.Quotes;
        if (IncludeBars) t |= ArchiveTables.Bars;
        if (IncludeTrades) t |= ArchiveTables.Trades;
        if (IncludeDepth) t |= ArchiveTables.Depth;
        return t;
    }

    private void LoadFromOptions()
    {
        var a = _archiveOpts.CurrentValue;
        Enabled = a.Enabled;
        Period = a.Period.ToString();
        IncludeQuotes = a.Tables.HasFlag(ArchiveTables.Quotes);
        IncludeBars = a.Tables.HasFlag(ArchiveTables.Bars);
        IncludeTrades = a.Tables.HasFlag(ArchiveTables.Trades);
        IncludeDepth = a.Tables.HasFlag(ArchiveTables.Depth);
        DailyCheckHourUtc = a.DailyCheckHourUtc;
        MaxPartBytes = a.MaxPartBytes;
        VerifyAfterUpload = a.VerifyAfterUpload;
        DeleteLocalAfterArchive = a.DeleteLocalAfterArchive;
        DefaultTargetKind = a.DefaultTargetKind ?? "saved";
        DefaultTargetChatRef = a.DefaultTargetChatRef ?? "";

        var s = _storeOpts.CurrentValue;
        RetentionEnabled = s.RetentionSweepEnabled;
        QuoteRetentionDays = s.QuoteRetentionDays;
        TradeRetentionDays = s.TradeRetentionDays;
        BarRetentionDays = s.BarRetentionDays;
        DepthRetentionDays = s.DepthRetentionDays;

        var t = _telegramOpts.CurrentValue;
        ApiId = t.ApiId;
        ApiHash = t.ApiHash;
        PhoneNumber = t.PhoneNumber;
        IsLoggedIn = _transport.IsReady;
        TelegramStatus = IsLoggedIn ? "Connected." : "Not logged in.";
    }
}
