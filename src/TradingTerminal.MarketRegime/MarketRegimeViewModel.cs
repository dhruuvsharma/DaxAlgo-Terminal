using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Regime;
using TradingTerminal.UI;

namespace TradingTerminal.MarketRegime;

/// <summary>
/// Read-only dashboard for the market-regime composite. Binds the live
/// <see cref="IMarketRegimeProvider.Updates"/> stream (marshalled onto the UI thread via the
/// captured <see cref="SynchronizationContext"/>) into a 0–100 gauge, the five-band label, the
/// ten category contribution bars, and a row of headline metrics. The manual Refresh command
/// forces an out-of-cadence recompute.
/// </summary>
public sealed partial class MarketRegimeViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketRegimeProvider _provider;
    private readonly ILogger<MarketRegimeViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;
    private readonly IDisposable _subscription;

    public MarketRegimeViewModel(IMarketRegimeProvider provider, ILogger<MarketRegimeViewModel> logger)
    {
        _provider = provider;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        _subscription = _provider.Updates.Subscribe(snapshot =>
        {
            if (_uiContext is null) ApplySnapshot(snapshot);
            else _uiContext.Post(_ => ApplySnapshot(snapshot), null);
        });
    }

    public ObservableCollection<RegimeCategoryRow> Categories { get; } = new();

    [ObservableProperty] private double _compositeScore;
    [ObservableProperty] private string _label = "—";
    [ObservableProperty] private string _scoreColor = "#888888";
    [ObservableProperty] private double? _previousScore;
    [ObservableProperty] private string _trend = "";
    [ObservableProperty] private bool _isRiskOff;
    [ObservableProperty] private bool _unavailable = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _lastUpdated = "never";

    // Header metrics (null → blank in the UI).
    [ObservableProperty] private string _vix = "N/A";
    [ObservableProperty] private string _putCall = "N/A";
    [ObservableProperty] private string _hySpread = "N/A";
    [ObservableProperty] private string _pctAbove200d = "N/A";
    [ObservableProperty] private string _yield10y = "N/A";
    [ObservableProperty] private string _fedRate = "N/A";
    [ObservableProperty] private string _cnnFearGreed = "N/A";
    [ObservableProperty] private string _financialStress = "N/A";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await _provider.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual regime refresh failed");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void ApplySnapshot(MarketRegimeSnapshot s)
    {
        Unavailable = s.Unavailable;
        if (s.Unavailable)
        {
            Label = "No data yet";
            LastUpdated = "never";
            Categories.Clear();
            return;
        }

        CompositeScore = s.CompositeScore;
        Label = s.Label;
        ScoreColor = ColorFor(s.CompositeScore);
        PreviousScore = s.PreviousScore;
        IsRiskOff = s.State.IsRiskOff();
        Trend = s.PreviousScore is { } prev
            ? (s.CompositeScore > prev ? $"▲ {s.CompositeScore - prev:F1}"
               : s.CompositeScore < prev ? $"▼ {prev - s.CompositeScore:F1}" : "—")
            : "";
        LastUpdated = s.GeneratedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

        var h = s.Header;
        Vix = Fmt(h.Vix, 1);
        PutCall = Fmt(h.PutCallRatio, 2);
        HySpread = h.HighYieldSpread is { } hy ? $"{hy:F2}%" : "N/A";
        PctAbove200d = h.PctAbove200dma is { } pa ? $"{pa:F0}%" : "N/A";
        Yield10y = h.Yield10y is { } y ? $"{y:F2}%" : "N/A";
        FedRate = h.FedFundsRate is { } f ? $"{f:F2}%" : "N/A";
        CnnFearGreed = h.CnnFearGreed?.ToString() ?? "N/A";
        FinancialStress = h.FinancialStressIndex is { } fsi
            ? $"{fsi:F2} ({h.FinancialStressLabel})" : "N/A";

        Categories.Clear();
        foreach (var c in s.Categories)
            Categories.Add(new RegimeCategoryRow(
                Name: DisplayName(c.Category),
                Score: c.Score,
                WeightPct: $"{c.Weight * 100:F0}%",
                Contribution: c.Contribution,
                Detail: c.Degraded ? $"{c.Detail} (degraded)" : c.Detail,
                BarColor: ColorFor(c.Score),
                BarWidth: c.Score));
    }

    private static string Fmt(double? v, int digits) =>
        v?.ToString("F" + digits, CultureInfo.InvariantCulture) ?? "N/A";

    /// <summary>Red (fear) → green (greed) ramp, matching the upstream Fear/Greed palette.</summary>
    private static string ColorFor(double score) => score switch
    {
        <= 20 => "#E74C3C",
        <= 40 => "#E67E22",
        <= 60 => "#F1C40F",
        <= 80 => "#2ECC71",
        _ => "#27AE60",
    };

    private static string DisplayName(RegimeCategory c) => c switch
    {
        RegimeCategory.Sentiment => "Sentiment",
        RegimeCategory.Volatility => "Volatility",
        RegimeCategory.Positioning => "Positioning",
        RegimeCategory.Trend => "Trend",
        RegimeCategory.Breadth => "Breadth",
        RegimeCategory.Momentum => "Momentum",
        RegimeCategory.Liquidity => "Liquidity",
        RegimeCategory.Credit => "Credit",
        RegimeCategory.Macro => "Macro",
        RegimeCategory.CrossAsset => "Cross-asset",
        _ => c.ToString(),
    };

    public void Dispose() => _subscription.Dispose();
}

/// <summary>One row in the category breakdown grid.</summary>
public sealed record RegimeCategoryRow(
    string Name,
    int Score,
    string WeightPct,
    double Contribution,
    string Detail,
    string BarColor,
    int BarWidth);
