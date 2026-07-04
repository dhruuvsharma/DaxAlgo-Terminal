using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Drives the combined <b>Bookmap + VolBook</b> window — one microstructure surface that fuses the two
/// platforms' feature sets:
/// <list type="bullet">
/// <item><b>Liquidity heatmap</b> — resting L2 size per (price row, time column), the Bookmap core.</item>
/// <item><b>Trade dots</b> — every print overlaid on the map, sized by volume, coloured by aggressor,
/// with <b>large lots</b> and refilling <b>icebergs</b> flagged.</item>
/// <item><b>Volume profile + VWAP + value area</b> — the VolBook core: a session volume-at-price
/// histogram (buy/sell split), the developing VWAP track, and POC / 70 % value-area edges.</item>
/// <item><b>Cumulative Volume Delta</b> — a CVD track plus per-column buy/sell delta bars.</item>
/// <item><b>DOM</b> — the live order-book ladder + resting-size histogram down the right edge.</item>
/// <item><b>Playback</b> — pause + scrub through the retained history; the window adds price-zoom.</item>
/// </list>
/// It reuses <see cref="SingleInstrumentHeatmapViewModelBase"/> plumbing (instrument picker, stream
/// lifecycle, dirty-flag render timer) but exposes the <em>raw</em> rolling buffers — the window draws
/// them itself onto a bitmap + overlay canvas for full control of the aesthetic.
/// </summary>
public sealed partial class BookmapHeatmapViewModel : SingleInstrumentHeatmapViewModelBase
{
    /// <summary>How many time columns are visible at once (the scrolling window width).</summary>
    public const int VisibleColumns = 320;

    /// <summary>How many columns are retained behind the view for playback / scrubbing.</summary>
    private const int MaxRetained = 1600;

    /// <summary>Wall-clock width of one time column (the selected timeframe) — snapshots inside it
    /// refresh the same column, so columns are time-uniform and a burst of L2 updates doesn't shred
    /// the time axis. A larger timeframe scrolls more slowly and is much lighter to render.</summary>
    private TimeSpan ColumnInterval => TimeSpan.FromSeconds(SelectedTimeframe?.Seconds ?? 1.0);

    /// <summary>How many recent prints stay drawn as dots.</summary>
    private const int MaxTrades = 4000;

    // Large-lot detection: a print is "large" once it dwarfs the rolling mean print size.
    private const int SizeWindow = 200;
    private const double LargeMultiple = 5.0;
    // Iceberg detection: the same (price, size) print repeating is the classic refill signature.
    private const int IcebergRepeat = 4;

    private readonly List<DepthSnapshot> _columns = new();
    private readonly List<ColumnStat> _stats = new();
    private readonly Queue<BookmapTrade> _trades = new();
    private readonly Dictionary<long, ProfileCell> _profile = new();

    private DateTime _lastColumnTime;
    private double _priceStep;

    // Session running aggregates.
    private double _cvd;
    private double _cumPv;
    private double _cumVol;
    private long _columnsCreated; // session column count (never trimmed) → drives Cumulative Average Volume

    // Rolling print-size stats for large-lot detection.
    private readonly Queue<long> _sizeWindow = new();
    private long _sizeSum;
    private double _lastTradePrice = double.NaN;
    private long _lastTradeSize;
    private int _repeatCount;

    public BookmapHeatmapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<BookmapHeatmapViewModel> logger)
        : base(repository, hub, ingest, selector, logger)
    {
        _selectedTimeframe = Timeframes[2]; // default 1 s — calmer + lighter than sub-second columns
        PresetNames = new ObservableCollection<string>(_presetStore.Names);
        Status = "Pick an instrument to stream Bookmap + VolBook (needs L2; volume features need the tape).";
    }

    /// <summary>Saved preset names for the toolbar picker.</summary>
    public ObservableCollection<string> PresetNames { get; }

    /// <summary>Column-width presets for the time axis (how much wall-clock each heatmap column spans).</summary>
    public IReadOnlyList<TimeframeOption> Timeframes { get; } = new[]
    {
        new TimeframeOption("250 ms", 0.25),
        new TimeframeOption("500 ms", 0.5),
        new TimeframeOption("1 s", 1),
        new TimeframeOption("2 s", 2),
        new TimeframeOption("5 s", 5),
        new TimeframeOption("10 s", 10),
        new TimeframeOption("30 s", 30),
        new TimeframeOption("1 m", 60),
    };

    [ObservableProperty] private TimeframeOption _selectedTimeframe;

    partial void OnSelectedTimeframeChanged(TimeframeOption value)
    {
        // Rebuild the heatmap/CVD columns at the new width going forward; session profile / VWAP /
        // CVD totals (and the trade dots) are timeframe-independent, so they carry over.
        _columns.Clear();
        _stats.Clear();
        _columnsCreated = 0;
        _lastColumnTime = default;
        ColumnsFilled = 0;
        MarkDirty();
    }

    // ── Quote / book read-outs ───────────────────────────────────────────────────────────────────
    [ObservableProperty] private double? _bestBid;
    [ObservableProperty] private double? _bestAsk;
    [ObservableProperty] private double? _mid;
    [ObservableProperty] private double? _spread;
    [ObservableProperty] private double? _lastPrice;
    [ObservableProperty] private int _columnsFilled;
    [ObservableProperty] private long _tradeCount;
    [ObservableProperty] private int _priceDecimals = 2;

    // ── VolBook read-outs ────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private double? _vwapDisplay;
    [ObservableProperty] private double _cvdDisplay;
    [ObservableProperty] private long _totalVolumeDisplay;
    [ObservableProperty] private double _cavDisplay; // cumulative average volume (per column)

    // ── Overlay toggles (bound to the toolbar; read by the window each redraw) ────────────────────
    [ObservableProperty] private bool _showVolumeProfile = true;
    [ObservableProperty] private bool _showVwap = true;
    [ObservableProperty] private bool _showValueArea = true;
    [ObservableProperty] private bool _showCvdPanel = true;
    [ObservableProperty] private bool _showTradeDots = true;
    [ObservableProperty] private bool _highlightLargeLots = true;
    [ObservableProperty] private bool _showOrderBook = true; // COB — Current Order Book column
    [ObservableProperty] private bool _showCav = true;       // CAV — Cumulative Average Volume strip

    partial void OnShowVolumeProfileChanged(bool value) => RequestRedraw();
    partial void OnShowVwapChanged(bool value) => RequestRedraw();
    partial void OnShowValueAreaChanged(bool value) => RequestRedraw();
    partial void OnShowCvdPanelChanged(bool value) => RequestRedraw();
    partial void OnShowTradeDotsChanged(bool value) => RequestRedraw();
    partial void OnHighlightLargeLotsChanged(bool value) => RequestRedraw();
    partial void OnShowOrderBookChanged(bool value) => RequestRedraw();
    partial void OnShowCavChanged(bool value) => RequestRedraw();

    // ── Playback (pause + scrub) ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _scrubPosition = 1.0; // 0 = oldest retained, 1 = live edge

    partial void OnIsPausedChanged(bool value)
    {
        if (!value) ScrubPosition = 1.0; // resuming snaps back to the live edge
        RequestRedraw();
    }

    partial void OnScrubPositionChanged(double value) => RequestRedraw();

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    // ── Presets (named view-option snapshots; see ToolPresetStore) ──────────────────────────────

    private readonly ToolPresetStore<BookmapPreset> _presetStore = new("bookmap-volbook");

    /// <summary>Editable preset-picker text: type a name and Save, or pick an existing preset to apply.</summary>
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new BookmapPreset(
            SelectedTimeframe.Label, ShowVolumeProfile, ShowVwap, ShowValueArea, ShowCvdPanel,
            ShowTradeDots, HighlightLargeLots, ShowOrderBook, ShowCav));
        RefreshPresetNames(selected: name);
        Logger.LogInformation("Bookmap preset '{Name}' saved", name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        Logger.LogInformation("Bookmap preset '{Name}' deleted", name);
    }

    private void ApplyPreset(BookmapPreset preset)
    {
        if (Timeframes.FirstOrDefault(t => t.Label == preset.TimeframeLabel) is { } tf)
            SelectedTimeframe = tf;
        ShowVolumeProfile = preset.ShowVolumeProfile;
        ShowVwap = preset.ShowVwap;
        ShowValueArea = preset.ShowValueArea;
        ShowCvdPanel = preset.ShowCvdPanel;
        ShowTradeDots = preset.ShowTradeDots;
        HighlightLargeLots = preset.HighlightLargeLots;
        ShowOrderBook = preset.ShowOrderBook;
        ShowCav = preset.ShowCav;
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG snapshots stay view-side) ─────────

    [RelayCommand]
    private async Task ExportProfileCsvAsync()
    {
        var profile = VolumeProfileSnapshot();
        if (profile.Length == 0) return;
        Array.Sort(profile, (a, b) => a.Price.CompareTo(b.Price));
        var va = ComputeValueArea();
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"# session volume profile · poc={va.Poc} vah={va.ValueAreaHigh} val={va.ValueAreaLow} total={va.TotalVolume}"));
        sb.AppendLine("price,buy_volume,sell_volume");
        foreach (var b in profile)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{b.Price},{b.BuyVolume},{b.SellVolume}"));
        await SaveCsvAsync("bookmap-profile", sb.ToString());
    }

    [RelayCommand]
    private async Task ExportFlowCsvAsync()
    {
        if (_stats.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,buy_volume,sell_volume,cvd,vwap,cav");
        for (var i = 0; i < _stats.Count && i < _columns.Count; i++)
        {
            var s = _stats[i];
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{_columns[i].TimestampUtc:O},{s.BuyVolume},{s.SellVolume},{s.Cvd},{s.Vwap},{s.AvgVolume}"));
        }
        await SaveCsvAsync("bookmap-flow", sb.ToString());
    }

    private async Task SaveCsvAsync(string baseName, string content)
    {
        try
        {
            var symbol = (SelectedInstrument?.Contract.Symbol ?? "book").Replace('/', '-').Replace(':', '-');
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"{baseName}-{symbol}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, content);
            Status = $"Exported → {path}";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Bookmap: CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Force a redraw without waiting for the next feed tick (e.g. a toggle while paused) —
    /// the render timer coalesces it within one tick.</summary>
    private void RequestRedraw() => MarkDirty();

    // ── Buffers exposed to the window (read on the UI thread) ─────────────────────────────────────

    /// <summary>All retained depth snapshots, oldest first (≥ <see cref="VisibleColumns"/> kept for scrub).</summary>
    public IReadOnlyList<DepthSnapshot> AllColumns => _columns;

    /// <summary>Per-column volume stats (buy/sell volume, CVD, developing VWAP), aligned to <see cref="AllColumns"/>.</summary>
    public IReadOnlyList<ColumnStat> AllStats => _stats;

    /// <summary>Snapshot of the retained trade dots (newest last).</summary>
    public BookmapTrade[] RecentTrades() => _trades.ToArray();

    /// <summary>The session volume profile (buy/sell volume per price bucket). Order is unspecified.</summary>
    public VolumeProfileBucket[] VolumeProfileSnapshot()
    {
        if (_profile.Count == 0) return Array.Empty<VolumeProfileBucket>();
        var arr = new VolumeProfileBucket[_profile.Count];
        int i = 0;
        foreach (var kv in _profile) arr[i++] = new VolumeProfileBucket(kv.Key * _priceStep, kv.Value.Buy, kv.Value.Sell);
        return arr;
    }

    /// <summary>POC and the 70 % value-area edges from the current session profile.</summary>
    public ValueArea ComputeValueArea()
    {
        if (_profile.Count == 0 || _priceStep <= 0) return default;

        var keys = _profile.Keys.ToArray();
        Array.Sort(keys);

        long total = 0;
        int pocIdx = 0;
        long pocVol = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            long v = Vol(keys[i]);
            total += v;
            if (v > pocVol) { pocVol = v; pocIdx = i; }
        }
        if (total == 0) return default;

        long acc = pocVol;
        int lo = pocIdx, hi = pocIdx;
        long target = (long)(total * 0.70);
        while (acc < target && (lo > 0 || hi < keys.Length - 1))
        {
            long below = lo > 0 ? Vol(keys[lo - 1]) : -1;
            long above = hi < keys.Length - 1 ? Vol(keys[hi + 1]) : -1;
            if (above >= below) { hi++; acc += Math.Max(0, above); }
            else { lo--; acc += Math.Max(0, below); }
        }

        return new ValueArea(keys[pocIdx] * _priceStep, keys[hi] * _priceStep, keys[lo] * _priceStep, total);

        long Vol(long k) { var c = _profile[k]; return c.Buy + c.Sell; }
    }

    // ── Stream wiring ────────────────────────────────────────────────────────────────────────────

    protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
    {
        AddStreamHandle(Ingest.Subscribe(instrument.Contract, broker));
        AddStreamHandle(Ingest.SubscribeTrades(instrument.Contract, broker)); // no-op handle without a tape
        PumpDepth(id, ct, OnSnapshot);
        PumpTrades(id, ct, OnTrade);
    }

    private void OnSnapshot(DepthSnapshot snapshot)
    {
        bool newColumn = _columns.Count == 0 || snapshot.TimestampUtc - _lastColumnTime >= ColumnInterval;
        if (newColumn)
        {
            _columnsCreated++;
            _columns.Add(snapshot);
            _stats.Add(new ColumnStat(0, 0, _cvd, CurrentVwap(), AvgVolumePerColumn()));
            _lastColumnTime = snapshot.TimestampUtc;
            while (_columns.Count > MaxRetained) { _columns.RemoveAt(0); _stats.RemoveAt(0); }
        }
        else
        {
            _columns[^1] = snapshot; // refresh the resting book to the latest within this time bucket
        }

        ColumnsFilled = _columns.Count;
        BestBid = snapshot.BestBid > 0 ? snapshot.BestBid : null;
        BestAsk = snapshot.BestAsk > 0 ? snapshot.BestAsk : null;
        Mid = BestBid is { } b && BestAsk is { } a ? (a + b) * 0.5 : null;
        Spread = BestBid is { } bb && BestAsk is { } aa ? aa - bb : null;
        double anchor = Mid ?? BestAsk ?? BestBid ?? 0;
        PriceDecimals = DecimalsFor(anchor);
        EnsurePriceStep(anchor);
    }

    private void OnTrade(TradePrint trade)
    {
        long size = trade.Size;
        EnsurePriceStep(trade.Price);

        var side = trade.Aggressor switch
        {
            AggressorSide.Buy => BubbleSide.Buy,
            AggressorSide.Sell => BubbleSide.Sell,
            _ => BubbleSide.Unknown,
        };

        // Large-lot: dwarfs the rolling mean print size.
        _sizeWindow.Enqueue(size);
        _sizeSum += size;
        while (_sizeWindow.Count > SizeWindow) _sizeSum -= _sizeWindow.Dequeue();
        double mean = _sizeWindow.Count > 0 ? (double)_sizeSum / _sizeWindow.Count : 0;
        bool large = _sizeWindow.Count >= 20 && size >= Math.Max(2, mean * LargeMultiple);

        // Iceberg: a (price, size) print repeating in place is the classic refill signature.
        if (trade.Price.Equals(_lastTradePrice) && size == _lastTradeSize) _repeatCount++;
        else _repeatCount = 1;
        _lastTradePrice = trade.Price;
        _lastTradeSize = size;
        bool iceberg = _repeatCount >= IcebergRepeat;

        _trades.Enqueue(new BookmapTrade(trade.EventTimeUtc, trade.Price, size, side, large, iceberg));
        while (_trades.Count > MaxTrades) _trades.Dequeue();

        // Session aggregates.
        _cumPv += trade.Price * size;
        _cumVol += size;
        if (side == BubbleSide.Sell) _cvd -= size; else _cvd += size; // unknown counts toward buy pressure

        // Volume profile bucket.
        long key = ProfileKey(trade.Price);
        var cell = _profile.TryGetValue(key, out var existing) ? existing : default;
        _profile[key] = side == BubbleSide.Sell
            ? cell with { Sell = cell.Sell + size }
            : cell with { Buy = cell.Buy + size };

        // Attribute the print's volume + delta to the current time column.
        if (_stats.Count > 0)
        {
            var st = _stats[^1];
            long bv = st.BuyVolume + (side == BubbleSide.Sell ? 0 : size);
            long sv = st.SellVolume + (side == BubbleSide.Sell ? size : 0);
            _stats[^1] = new ColumnStat(bv, sv, _cvd, CurrentVwap(), AvgVolumePerColumn());
        }

        LastPrice = trade.Price;
        TradeCount++;
        CvdDisplay = _cvd;
        VwapDisplay = _cumVol > 0 ? _cumPv / _cumVol : null;
        TotalVolumeDisplay = (long)_cumVol;
        CavDisplay = AvgVolumePerColumn();
    }

    protected override void ResetBuffers()
    {
        _columns.Clear();
        _stats.Clear();
        _trades.Clear();
        _profile.Clear();
        _sizeWindow.Clear();
        _sizeSum = 0;
        _lastTradePrice = double.NaN;
        _lastTradeSize = 0;
        _repeatCount = 0;
        _cvd = _cumPv = _cumVol = 0;
        _columnsCreated = 0;
        _priceStep = 0;
        _lastColumnTime = default;

        ColumnsFilled = 0;
        TradeCount = 0;
        TotalVolumeDisplay = 0;
        CvdDisplay = 0;
        CavDisplay = 0;
        VwapDisplay = null;
        BestBid = BestAsk = Mid = Spread = LastPrice = null;
        if (!IsPaused) ScrubPosition = 1.0;
    }

    private double CurrentVwap() => _cumVol > 0 ? _cumPv / _cumVol : double.NaN;

    /// <summary>Cumulative Average Volume — session traded volume averaged over the columns elapsed.</summary>
    private double AvgVolumePerColumn() => _columnsCreated > 0 ? _cumVol / _columnsCreated : 0;

    private long ProfileKey(double price) => _priceStep > 0 ? (long)Math.Round(price / _priceStep) : 0;

    /// <summary>Pick a session volume-profile bucket step from the instrument's price magnitude
    /// (≈ price/1000 granularity), set once on the first quote/print.</summary>
    private void EnsurePriceStep(double price)
    {
        if (_priceStep > 0 || price <= 0) return;
        _priceStep = Math.Max(Math.Pow(10, Math.Floor(Math.Log10(price)) - 3), 1e-9);
    }

    /// <summary>Sensible price-format precision from the instrument's price magnitude.</summary>
    private static int DecimalsFor(double price)
    {
        var p = Math.Abs(price);
        if (p == 0) return 2;
        if (p >= 100) return 2;
        if (p >= 1) return 3;
        if (p >= 0.01) return 5;
        return 8;
    }
}

/// <summary>Which side initiated a trade — drives the dot / delta colour.</summary>
public enum BubbleSide
{
    Unknown = 0,
    Buy,
    Sell,
}

/// <summary>One retained trade print for the Bookmap dot overlay, with classification flags.</summary>
public readonly record struct BookmapTrade(DateTime Time, double Price, long Size, BubbleSide Side, bool Large, bool Iceberg);

/// <summary>Per-time-column executed-volume stats, aligned to the depth-snapshot columns.
/// <paramref name="AvgVolume"/> is the session Cumulative Average Volume snapshot at that column.</summary>
public readonly record struct ColumnStat(long BuyVolume, long SellVolume, double Cvd, double Vwap, double AvgVolume);

/// <summary>One price bucket of the session volume profile (buy/sell executed volume).</summary>
public readonly record struct VolumeProfileBucket(double Price, long BuyVolume, long SellVolume);

/// <summary>Point of control + 70 % value-area edges derived from the session volume profile.</summary>
public readonly record struct ValueArea(double Poc, double ValueAreaHigh, double ValueAreaLow, long TotalVolume);

/// <summary>A time-axis column-width preset (label + seconds per heatmap column).</summary>
public sealed record TimeframeOption(string Label, double Seconds)
{
    public override string ToString() => Label;
}

/// <summary>A named snapshot of the Bookmap + VolBook window's view options, persisted per user by
/// <see cref="ToolPresetStore{T}"/> (LocalAppData\DaxAlgo Terminal\tool-presets\bookmap-volbook.json).</summary>
public sealed record BookmapPreset(
    string TimeframeLabel,
    bool ShowVolumeProfile,
    bool ShowVwap,
    bool ShowValueArea,
    bool ShowCvdPanel,
    bool ShowTradeDots,
    bool HighlightLargeLots,
    bool ShowOrderBook,
    bool ShowCav);

/// <summary>Mutable-by-copy accumulator cell for a volume-profile price bucket.</summary>
internal readonly record struct ProfileCell(long Buy, long Sell);
