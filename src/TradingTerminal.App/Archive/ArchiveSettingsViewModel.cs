using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;
using TradingTerminal.UI;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Settings tab for the market-data archive. Three sections: Telegram credentials + login,
/// schedule + retention knobs, and a manual "Offload now" range picker. Saving writes the
/// per-user JSON; IOptionsMonitor surfaces the change to the running schedule service.
/// </summary>
public sealed partial class ArchiveSettingsViewModel : ViewModelBase
{
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOpts;
    private readonly IOptionsMonitor<TelegramArchiveOptions> _telegramOpts;
    private readonly TelegramArchiveTransport _transport;
    private readonly IMarketDataArchiver _archiver;
    private readonly ILogger<ArchiveSettingsViewModel> _logger;

    public ArchiveSettingsViewModel(
        IOptionsMonitor<ArchiveOptions> archiveOpts,
        IOptionsMonitor<TelegramArchiveOptions> telegramOpts,
        TelegramArchiveTransport transport,
        IMarketDataArchiver archiver,
        ILogger<ArchiveSettingsViewModel> logger)
    {
        _archiveOpts = archiveOpts;
        _telegramOpts = telegramOpts;
        _transport = transport;
        _archiver = archiver;
        _logger = logger;

        LoadFromOptions();
        // Sane defaults for the manual offload — last completed week.
        var (from, to) = ArchivePeriodMath.ClosedPeriod(DateTime.UtcNow, ArchivePeriod.Weekly);
        ManualFromUtc = from;
        ManualToUtc = to;
    }

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
            ArchiveUserFile.Save(next, tg);
            StatusMessage = $"Saved to {ArchiveUserFile.Path}";
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
            StatusMessage = $"Offloading [{ManualFromUtc:s} → {ManualToUtc:s})…";
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

        var t = _telegramOpts.CurrentValue;
        ApiId = t.ApiId;
        ApiHash = t.ApiHash;
        PhoneNumber = t.PhoneNumber;
        IsLoggedIn = _transport.IsReady;
        TelegramStatus = IsLoggedIn ? "Connected." : "Not logged in.";
    }
}
